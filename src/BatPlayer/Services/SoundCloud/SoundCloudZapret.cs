using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Антиблокировка SoundCloud пакетного уровня: автоматический перебор стратегий
/// winws.exe (zapret, WinDivert) из официального набора Flowseal, ограниченный
/// доменами SoundCloud. Методы — fake/multisplit/hostfakesplit/fakedsplit/
/// syndata/multidisorder с fooling ts/badseq/md5sig — работают против провайдеров,
/// собирающих TCP-поток целиком (юзерспейс-фрагментация из <see cref="DpiBypassProxy"/>
/// против реассемблера бессильна).
///
/// Как работает: при недоступности всех транспортов приложение поднимает повышенный
/// хелпер (helper.ps1, ОДИН UAC-запрос за сессию) и по командному файлу перебирает
/// пресеты; после каждого — пробное TLS-рукопожатие с настоящим SNI к api-v2
/// (winws модифицирует пакеты на уровне драйвера для совпавших с hostlist хостов).
/// Найденная рабочая стратегия сохраняется и применяется первой в следующих сессиях.
/// Хелпер и winws завершаются сами, когда процесс плеера закрыт.
/// </summary>
internal static class SoundCloudZapret
{
    private static readonly object Lock = new();

    private static Process? _helper;
    private static bool _helperDeclined;
    private static DateTime? _lastCycleFailedAtUtc;

    // ===== Состояние обхода для интерфейса =====

    /// <summary>Фаза перебора стратегий обхода — то, что показывает индикатор в UI.</summary>
    public enum ZapretPhase
    {
        /// <summary>Цикл запущен: поднимаем хелпер (UAC) / готовим перебор.</summary>
        Starting,
        /// <summary>Пробуем очередную стратегию winws.</summary>
        TestingPreset,
        /// <summary>Рабочая стратегия найдена, обход активен.</summary>
        Succeeded,
        /// <summary>Ни одна стратегия не пробила блокировку.</summary>
        Failed,
        /// <summary>Хелпер не запущен: UAC отклонён.</summary>
        HelperDeclined,
    }

    /// <summary>Снимок фазы обхода для UI. PresetName — имя стратегии (Succeeded/TestingPreset),
    /// PresetNumber/PresetCount — позиция в переборе (1-based).</summary>
    public sealed record ZapretUiState(ZapretPhase Phase, string? PresetName, int PresetNumber, int PresetCount);

    /// <summary>Прогресс обхода блокировки (подписчик — MainViewModel: индикатор в тайтл-баре).
    /// События летят из фоновых потоков цикла — подписчик обязан маршалить в UI-поток.</summary>
    public static event Action<ZapretUiState>? UiStateChanged;

    private static void RaiseUi(ZapretPhase phase, string? presetName = null, int presetNumber = 0, int presetCount = 0)
    {
        try { UiStateChanged?.Invoke(new ZapretUiState(phase, presetName, presetNumber, presetCount)); }
        catch { // подписчик UI не должен ронять цикл обхода
        }
    }

    /// <summary>Пауза между проваленными циклами перебора: каждый цикл — минуты
    /// пробных рукопожатий, повторять их на каждый клик нельзя.</summary>
    private static readonly TimeSpan FailedCycleCooldown = TimeSpan.FromMinutes(5);
    private static TaskCompletionSource<bool>? _cycleTcs;

    private static string BaseDir => Path.Combine(AppContext.BaseDirectory, "Tools", "zapret");
    private static string PresetsFile => Path.Combine(BaseDir, "presets.json");
    private static string HelperFile => Path.Combine(BaseDir, "helper.ps1");
    private static string BinDir => Path.Combine(BaseDir, "bin");
    private static string CmdFile => Path.Combine(BaseDir, "command.txt");
    private static string StatusFile => Path.Combine(BaseDir, "status.txt");
    private static string LastWorkingFile => Path.Combine(App.AppDataDir, "zapret_last_preset.txt");

    private sealed record Preset(string Name, string Args);

    private static List<Preset>? _presets;

    private static List<Preset> LoadPresets()
    {
        if (_presets != null) return _presets;
        try
        {
            var json = File.ReadAllText(PresetsFile);
            var doc = JsonSerializer.Deserialize<List<JsonElement>>(json);
            _presets = doc?.Select(d => new Preset(
                    d.GetProperty("name").GetString() ?? "?",
                    d.GetProperty("args").GetString() ?? ""))
                .Where(p => p.Args.Length > 0)
                .ToList() ?? new List<Preset>();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud zapret: presets.json load failed");
            _presets = new List<Preset>();
        }
        return _presets;
    }

    /// <summary>
    /// Запустить перебор стратегий (идемпотентно: параллельные вызовы ждут результат
    /// одного цикла). true — найдена рабочая стратегия, winws активен, direct-транспорт
    /// проходит через неё. false — хелпер не запущен (UAC отказ/выключено) либо ни
    /// одна стратегия не пробила блокировку.
    /// </summary>
    public static Task<bool> EnsureStartedAsync(bool enabledBySetting)
    {
        lock (Lock)
        {
            if (_cycleTcs != null) return _cycleTcs.Task;
            if (_helperDeclined || !enabledBySetting)
                return Task.FromResult(false);
            // Проваленный цикл недавно повторять не спешим (каждый — минуты probing).
            if (_lastCycleFailedAtUtc.HasValue
                && DateTime.UtcNow - _lastCycleFailedAtUtc.Value < FailedCycleCooldown)
                return Task.FromResult(false);
            _cycleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = Task.Run(() => RunCycle(_cycleTcs!));
        return _cycleTcs.Task;
    }

    /// <summary>Сбросить проваленный цикл — новый клик по треку после кулдауна может
    /// запустить перебор заново (сеть/VPN могли измениться).</summary>
    public static void ResetFailedCycle()
    {
        lock (Lock)
        {
            if (_cycleTcs != null && _cycleTcs.Task.IsCompleted && _cycleTcs.Task.Result == false)
                _cycleTcs = null;
        }
    }

    private static Task? _watchdog;

    /// <summary>
    /// Сторож антиблокировки (вызывается при запуске приложения, живёт всю сессию).
    /// Раз в минуту проверяет прямой доступ к api-v2:
    ///  - доступен (VPN включён / провайдер не режет) — ничего не делает, ноль нагрузки;
    ///  - недоступен, а winws ещё не запущен — запускает перебор стратегий
    ///    (один UAC-запрос за сессию); покрытие вероятностных блокировок,
    ///    когда часть запросов «пролетает» и триггер «все транспорты упали» не срабатывает.
    /// </summary>
    public static void StartWatchdog(bool enabledBySetting)
    {
        lock (Lock)
        {
            if (_watchdog != null) return;
            _watchdog = Task.Run(() => WatchdogLoopAsync(enabledBySetting));
        }
    }

    private static async Task WatchdogLoopAsync(bool enabledBySetting)
    {
        if (!enabledBySetting)
        {
            Logger.Info("SoundCloud watchdog: отключен настройкой");
            return;
        }

        Logger.Info("SoundCloud watchdog: запущен (проверка доступа раз в минуту)");
        try
        {
            // Первая проверка сразу после старта приложения.
            await TickAsync().ConfigureAwait(false);

            while (true)
            {
                await Task.Delay(60_000).ConfigureAwait(false);
                await TickAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud zapret watchdog crashed");
        }
    }

    private static async Task TickAsync()
    {
        try
        {
            // Блокировка вероятностная: часть соединений проскакивает. Одна проба
            // врёт — делаем три реальных GET к api-v2; живым считаем доступ,
            // отвечающий минимум на 2 из 3.
            var alive = false;
            for (var i = 0; i < 3 && !alive; i++)
            {
                if (await ProbeAllAsync().ConfigureAwait(false)) alive = true;
                else await Task.Delay(400).ConfigureAwait(false);
            }
            if (alive)
            {
                // Прямой доступ считаем работающим (VPN/провайдер) — winws не нужен.
                return;
            }

            Logger.Info("SoundCloud watchdog: доступ заблокирован — запускаю перебор стратегий");
            var cycleOk = await EnsureStartedAsync(true).ConfigureAwait(false);
            if (!cycleOk)
                Logger.Warn("SoundCloud watchdog: перебор не дал результата — SC-треки без VPN недоступны");
        }
        catch (Exception ex)
        {
            Logger.Warn($"SoundCloud watchdog tick failed: {ex.Message}");
        }
    }

    private static async Task RunCycle(TaskCompletionSource<bool> done)
    {
        try
        {
            var presets = LoadPresets();
            if (presets.Count == 0)
            {
                done.TrySetResult(false);
                return;
            }

            // Начинаем с прошлой рабочей стратегии (если сохранена).
            var lastName = ReadLastWorking();
            if (!string.IsNullOrEmpty(lastName))
            {
                var idx = presets.FindIndex(p => string.Equals(p.Name, lastName, StringComparison.Ordinal));
                if (idx > 0)
                {
                    var first = presets[idx];
                    presets.RemoveAt(idx);
                    presets.Insert(0, first);
                }
            }

            RaiseUi(ZapretPhase.Starting);
            if (!await EnsureHelperAsync().ConfigureAwait(false))
            {
                RaiseUi(ZapretPhase.HelperDeclined);
                done.TrySetResult(false);
                return;
            }

            var presetNumber = 0;
            foreach (var preset in presets)
            {
                presetNumber++;
                RaiseUi(ZapretPhase.TestingPreset, preset.Name, presetNumber, presets.Count);
                if (!await SendCommandAsync("preset:" + ToB64(preset.Args), TimeSpan.FromSeconds(6))
                        .ConfigureAwait(false))
                    break; // хелпер умер — цикл бессмысленен
                if (ReadStatus() != "applied") continue;

                // Проба: настоящее TLS-рукопожатие с SNI api-v2 — winws desync-ит поток.
                var works = false;
                for (var attempt = 0; attempt < 2 && !works; attempt++)
                    works = await ProbeAllAsync().ConfigureAwait(false);

                if (works)
                {
                    SaveLastWorking(preset.Name);
                    Logger.Info($"SoundCloud zapret: рабочая стратегия найдена — \"{preset.Name}\"");
                    RaiseUi(ZapretPhase.Succeeded, preset.Name);
                    done.TrySetResult(true);
                    return;
                }
                Logger.Info($"SoundCloud zapret: стратегия \"{preset.Name}\" не пробила — следующая");
            }

            // Ни одна не сработала: останавливаем winws (хелпер остаётся на случай повтора).
            await SendCommandAsync("stop", TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            lock (Lock) _lastCycleFailedAtUtc = DateTime.UtcNow;
            Logger.Warn("SoundCloud zapret: ни одна из стратегий не пробила блокировку");
            RaiseUi(ZapretPhase.Failed);
            done.TrySetResult(false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud zapret cycle failed");
            RaiseUi(ZapretPhase.Failed);
            done.TrySetResult(false);
        }
    }

    // ============ Хелпер (повышенный PowerShell) ============

    private static async Task<bool> EnsureHelperAsync()
    {
        lock (Lock)
        {
            if (_helper is { HasExited: false }) return true;
        }

        if (!File.Exists(HelperFile) || !File.Exists(PresetsFile))
        {
            Logger.Warn("SoundCloud zapret: helper/presets files missing (Tools/zapret)");
            return false;
        }

        // Статус-файл от прошлой сессии не должен вводить в заблуждение.
        try { File.Delete(StatusFile); } catch { }
        try { File.Delete(CmdFile); } catch { }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{HelperFile}\"",
                UseShellExecute = true, // Verb=runas → UAC
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            var proc = Process.Start(psi);
            if (proc == null)
            {
                Decline();
                return false;
            }

            lock (Lock) _helper = proc;
            await Task.Delay(1500).ConfigureAwait(false);
            if (proc.HasExited)
            {
                Decline();
                Logger.Warn("SoundCloud zapret: helper exited immediately (UAC declined?)");
                return false;
            }
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Decline();
            Logger.Warn("SoundCloud zapret: UAC declined by user");
            return false;
        }
        catch (Exception ex)
        {
            Decline();
            Logger.Error(ex, "SoundCloud zapret helper start failed");
            return false;
        }
    }

    private static void Decline()
    {
        lock (Lock) _helperDeclined = true;
    }

    // ============ Файловый протокол с хелпером ============

    private static async Task<bool> SendCommandAsync(string command, TimeSpan timeout)
    {
        try
        {
            try { File.Delete(StatusFile); } catch { }
            await File.WriteAllTextAsync(CmdFile, command).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250).ConfigureAwait(false);
                if (!File.Exists(CmdFile)) // хелпер съел команду и ответил
                    return ReadStatus() == "applied";
            }
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn($"SoundCloud zapret command failed: {ex.Message}");
            return false;
        }
    }

    private static string ReadStatus()
    {
        try { return File.ReadAllText(StatusFile).Trim(); }
        catch { return string.Empty; }
    }

    private static string ToB64(string s)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    // ============ Проба работоспособности ============

    private static HttpClient? _probeClient;

    /// <summary>Цели пробы. Главный сайт у ряда провайдеров дропается по SNI детерминированно,
    /// при этом api-v2 и медиа-CDN могут ходить напрямую — проба только по api-v2 не замечает
    /// блокировку (watchdog молчит, а сайт и стриминг лежат). Живым считаем транспорт,
    /// отвечающий по ОБЕМ целям.</summary>
    private static readonly string[] ProbeUrls =
    {
        "https://soundcloud.com/",
        "https://api-v2.soundcloud.com/me",
    };

    private static async Task<bool> ProbeAllAsync()
    {
        foreach (var url in ProbeUrls)
        {
            if (!await ProbeWorksAsync(url).ConfigureAwait(false)) return false;
        }
        return true;
    }

    /// <summary>Проба транспорта = реальный GET. ЛЮБОЙ HTTP-ответ (в т.ч. 401/403/404)
    /// означает, что TLS+HTTP до SoundCloud прошли (winws-стратегия пробила DPI или блокировки
    /// нет); таймаут/обрыв = соединение дропнуто.</summary>
    private static async Task<bool> ProbeWorksAsync(string url)
    {
        try
        {
            if (_probeClient == null)
            {
                var handler = new System.Net.Http.SocketsHttpHandler
                {
                    UseCookies = false,
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                };
                _probeClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                _probeClient.DefaultRequestHeaders.UserAgent.ParseAdd(SoundCloudService.UserAgent);
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var resp = await _probeClient.GetAsync(url, cts.Token).ConfigureAwait(false);
            return (int)resp.StatusCode >= 100; // любой HTTP-ответ = транспорт жив
        }
        catch
        {
            return false;
        }
    }

    // ============ Запоминание рабочей стратегии ============

    private static string? ReadLastWorking()
    {
        try { return File.ReadAllText(LastWorkingFile).Trim(); }
        catch { return null; }
    }

    private static void SaveLastWorking(string name)
    {
        try { File.WriteAllText(LastWorkingFile, name); } catch { }
        lock (Lock) _lastCycleFailedAtUtc = null;
    }
}
