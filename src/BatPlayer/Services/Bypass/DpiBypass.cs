using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BatPlayer.Services;

/// <summary>
/// Built-in DPI bypass, fully automatic (no UI). Packet-level engine (WinDivert,
/// zapret-style presets) applied system-wide to the hosts in Tools/bypass/lists/hosts.txt.
///
/// A watchdog probes SoundCloud once a minute. When direct access is blocked, the
/// service starts the elevated helper (Tools/bypass/helper.ps1, one UAC prompt per
/// session) and runs a preset scan: each preset from presets.json is applied in turn,
/// probed over HTTPS and scored by reachable targets and latency. The best preset
/// stays active and is remembered for the next session; if it stops working the scan
/// runs again. The helper and the engine exit when the player process closes.
/// </summary>
internal static class DpiBypass
{
    private static readonly object Lock = new();

    private static Process? _helper;
    private static bool _helperDeclined;
    private static DateTime? _lastCycleFailedAtUtc;
    private static TaskCompletionSource<bool>? _cycleTcs;
    private static Task? _watchdog;

    // Cooldown between failed scans: a full scan takes minutes and must not repeat
    // on every click.
    private static readonly TimeSpan FailedCycleCooldown = TimeSpan.FromMinutes(5);

    private static string BaseDir => Path.Combine(AppContext.BaseDirectory, "Tools", "bypass");
    private static string PresetsFile => Path.Combine(BaseDir, "presets.json");
    private static string HelperFile => Path.Combine(BaseDir, "helper.ps1");
    private static string CmdFile => Path.Combine(BaseDir, "command.txt");
    private static string StatusFile => Path.Combine(BaseDir, "status.txt");
    private static string LastWorkingFile => Path.Combine(App.AppDataDir, "bypass_last_preset.txt");

    private sealed record Preset(string Name, string Args);

    private sealed record ProbeResult(bool Ok, long Ms);

    private sealed record PresetScore(int Index, string Name, int ScOk, int AllOk, long AvgMs);

    private static List<Preset>? _presets;

    private static List<Preset> LoadPresets()
    {
        if (_presets != null) return _presets;
        try
        {
            var doc = JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(PresetsFile));
            _presets = doc?.Select(d => new Preset(
                    d.GetProperty("name").GetString() ?? "?",
                    d.GetProperty("args").GetString() ?? ""))
                .Where(p => p.Args.Length > 0)
                .ToList() ?? new List<Preset>();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "DPI bypass: presets.json load failed");
            _presets = new List<Preset>();
        }
        return _presets;
    }

    /// <summary>
    /// Make SoundCloud reachable (idempotent: concurrent callers await one cycle).
    /// true — a working preset is active; false — UAC declined, disabled, or nothing worked.
    /// </summary>
    public static Task<bool> EnsureStartedAsync(bool enabled)
    {
        lock (Lock)
        {
            if (_cycleTcs != null) return _cycleTcs.Task;
            if (_helperDeclined || !enabled)
                return Task.FromResult(false);
            if (_lastCycleFailedAtUtc.HasValue
                && DateTime.UtcNow - _lastCycleFailedAtUtc.Value < FailedCycleCooldown)
                return Task.FromResult(false);
            _cycleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = Task.Run(() => RunCycle(_cycleTcs!));
        return _cycleTcs.Task;
    }

    /// <summary>
    /// Bypass watchdog, started once at application startup. Every minute it checks
    /// direct access to SoundCloud; when it is blocked, a bypass cycle is started.
    /// Probabilistic blocking lets some requests through — hence three real GETs.
    /// </summary>
    public static void StartWatchdog(bool enabled)
    {
        lock (Lock)
        {
            if (_watchdog != null) return;
            _watchdog = Task.Run(() => WatchdogLoopAsync(enabled));
        }
    }

    private static async Task WatchdogLoopAsync(bool enabled)
    {
        if (!enabled)
        {
            Logger.Info("DPI bypass watchdog: disabled by setting");
            return;
        }

        Logger.Info("DPI bypass watchdog: started (SoundCloud check every minute)");
        try
        {
            await TickAsync().ConfigureAwait(false);
            while (true)
            {
                await Task.Delay(60_000).ConfigureAwait(false);
                await TickAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "DPI bypass watchdog crashed");
        }
    }

    private static async Task TickAsync()
    {
        try
        {
            var alive = false;
            for (var i = 0; i < 3 && !alive; i++)
            {
                if (await SoundCloudReachableAsync().ConfigureAwait(false)) alive = true;
                else await Task.Delay(400).ConfigureAwait(false);
            }
            if (alive) return; // direct access works (VPN / unblocked provider)

            Logger.Info("DPI bypass watchdog: access blocked — starting preset scan");
            if (!await EnsureStartedAsync(true).ConfigureAwait(false))
                Logger.Warn("DPI bypass watchdog: scan found no working preset");
        }
        catch (Exception ex)
        {
            Logger.Warn($"DPI bypass watchdog tick failed: {ex.Message}");
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

            if (!await EnsureHelperAsync().ConfigureAwait(false))
            {
                done.TrySetResult(false);
                return;
            }

            // Fast path: try the remembered preset first.
            var last = ReadLastWorking();
            if (!string.IsNullOrEmpty(last))
            {
                var saved = presets.FirstOrDefault(p => string.Equals(p.Name, last, StringComparison.Ordinal));
                if (saved != null && await ApplyPresetAsync(saved).ConfigureAwait(false)
                    && (await ProbeAsync().ConfigureAwait(false)).ScOk == 2)
                {
                    Logger.Info($"DPI bypass: saved preset works — \"{saved.Name}\"");
                    done.TrySetResult(true);
                    return;
                }
            }

            // Full scan: apply each preset, probe, score.
            var scores = new List<PresetScore>();
            for (var i = 0; i < presets.Count; i++)
            {
                var preset = presets[i];
                if (!await ApplyPresetAsync(preset).ConfigureAwait(false))
                    break; // helper died — the scan is pointless

                var score = await ProbeAsync().ConfigureAwait(false);
                if (score.ScOk == 0)
                    score = await ProbeAsync().ConfigureAwait(false); // one retry for a total miss
                scores.Add(score with { Index = i, Name = preset.Name });
                Logger.Info($"DPI bypass: preset \"{preset.Name}\" ok={score.AllOk}/{ProbeTargets.Length} sc={score.ScOk} avg={score.AvgMs}ms");
            }

            var best = scores
                .Where(s => s.ScOk > 0)
                .OrderByDescending(s => s.ScOk)
                .ThenByDescending(s => s.AllOk)
                .ThenBy(s => s.AvgMs <= 0 ? long.MaxValue : s.AvgMs)
                .FirstOrDefault();

            if (best == null)
            {
                await SendCommandAsync("stop", TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                lock (Lock) _lastCycleFailedAtUtc = DateTime.UtcNow;
                Logger.Warn("DPI bypass: no preset made SoundCloud reachable");
                done.TrySetResult(false);
                return;
            }

            // Re-apply the winner and confirm it before remembering it.
            var winner = presets[best.Index];
            if (await ApplyPresetAsync(winner).ConfigureAwait(false)
                && (await ProbeAsync().ConfigureAwait(false)).ScOk == 2)
            {
                SaveLastWorking(winner.Name);
                Logger.Info($"DPI bypass: best preset active — \"{winner.Name}\"");
                done.TrySetResult(true);
                return;
            }

            // The winner did not survive the re-check: treat as a failed cycle.
            await SendCommandAsync("stop", TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            lock (Lock) _lastCycleFailedAtUtc = DateTime.UtcNow;
            done.TrySetResult(false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "DPI bypass cycle failed");
            done.TrySetResult(false);
        }
    }

    // ======================= Elevated helper =======================

    private static async Task<bool> EnsureHelperAsync()
    {
        lock (Lock)
        {
            if (_helper is { HasExited: false }) return true;
        }

        if (!File.Exists(HelperFile) || !File.Exists(PresetsFile))
        {
            Logger.Warn("DPI bypass: helper/presets files missing (Tools/bypass)");
            return false;
        }

        // Stale protocol files from a previous session must not mislead the scan.
        try { File.Delete(StatusFile); } catch { }
        try { File.Delete(CmdFile); } catch { }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{HelperFile}\"",
                UseShellExecute = true, // Verb=runas -> UAC
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            var proc = Process.Start(psi);
            if (proc == null)
            {
                MarkDeclined();
                return false;
            }

            lock (Lock) _helper = proc;
            await Task.Delay(1500).ConfigureAwait(false);
            if (proc.HasExited)
            {
                MarkDeclined();
                Logger.Warn("DPI bypass: helper exited immediately (UAC declined?)");
                return false;
            }
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MarkDeclined();
            Logger.Warn("DPI bypass: UAC declined by user");
            return false;
        }
        catch (Exception ex)
        {
            MarkDeclined();
            Logger.Error(ex, "DPI bypass helper start failed");
            return false;
        }
    }

    private static void MarkDeclined()
    {
        lock (Lock) _helperDeclined = true;
    }

    // ==================== File protocol with the helper ====================

    private static async Task<bool> ApplyPresetAsync(Preset preset)
    {
        if (!await SendCommandAsync("preset:" + ToB64(preset.Args), TimeSpan.FromSeconds(6)).ConfigureAwait(false))
            return false;
        return ReadStatus() == "applied";
    }

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
                if (!File.Exists(CmdFile)) // helper consumed the command and answered
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn($"DPI bypass command failed: {ex.Message}");
            return false;
        }
    }

    private static string ReadStatus()
    {
        try { return File.ReadAllText(StatusFile).Trim(); }
        catch { return string.Empty; }
    }

    private static string ToB64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    // ============================ Probing ============================

    private static HttpClient? _probeClient;

    /// <summary>
    /// Probe targets. The SoundCloud pair is the player's core (site + api-v2; some
    /// providers block only one of them). The YouTube pair is scored for the rest of
    /// the system, where the engine is active system-wide; a total miss there does
    /// not disqualify a preset.
    /// </summary>
    private static readonly string[] ProbeTargets =
    {
        "https://soundcloud.com/",
        "https://api-v2.soundcloud.com/me",
        "https://www.youtube.com/",
        "https://youtubei.googleapis.com/",
    };

    private static async Task<PresetScore> ProbeAsync()
    {
        var results = new ProbeResult[ProbeTargets.Length];
        await Parallel.ForAsync(0, ProbeTargets.Length, async (i, ct) =>
            results[i] = await ProbeHostAsync(ProbeTargets[i], ct).ConfigureAwait(false));

        var scOk = results.Take(2).Count(r => r.Ok);
        var allOk = results.Count(r => r.Ok);
        var avg = allOk > 0 ? (long)results.Where(r => r.Ok).Average(r => r.Ms) : 0;
        return new PresetScore(0, string.Empty, scOk, allOk, avg);
    }

    /// <summary>SoundsCloud-only liveness check used by the watchdog.</summary>
    private static async Task<bool> SoundCloudReachableAsync()
    {
        foreach (var url in ProbeTargets.Take(2))
        {
            var r = await ProbeHostAsync(url, CancellationToken.None).ConfigureAwait(false);
            if (!r.Ok) return false;
        }
        return true;
    }

    /// <summary>Any HTTP response (even 401/403) means TLS+HTTP made it through the
    /// DPI; timeout/reset means the connection was dropped.</summary>
    private static async Task<ProbeResult> ProbeHostAsync(string url, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (_probeClient == null)
            {
                var handler = new HttpClientHandler
                {
                    UseCookies = false,
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                };
                _probeClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
                _probeClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var resp = await _probeClient.GetAsync(url, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return new ProbeResult(true, sw.ElapsedMilliseconds);
        }
        catch
        {
            return new ProbeResult(false, sw.ElapsedMilliseconds);
        }
    }

    // ===================== Remembered working preset =====================

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
