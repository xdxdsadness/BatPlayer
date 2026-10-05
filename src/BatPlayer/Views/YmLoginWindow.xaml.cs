using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BatPlayer.Localization;
using BatPlayer.Services;
using BatPlayer.Services.YandexMusic;

namespace BatPlayer.Views;

/// <summary>
/// Окно входа в Яндекс Музыку — OAuth Device Flow Яндекс ID (без WebView2).
///
/// Почему не implicit-редирект: у клиента Яндекс Музыки согласие на oauth.yandex.ru
/// работает только с официальным client_id Android-приложения, а токен приходит
/// на music.yandex.ru, который мгновенно переехивает дальше (fragment теряется) —
/// схема хрупкая. Device Flow надёжен: плеер запрашивает код (POST device/code),
/// показывает его пользователю, тот вводит код на официальной странице
/// oauth.yandex.ru/device в браузере, а плеер опрашивает POST token (grant_type=
/// device_code) с рекомендованным интервалом до подтверждения/истечения кода.
///
/// Состояния: ЗапросКода → Ожидание → (Подтверждено — окно закрывается) |
/// (Отказано/Сеть — статус + «Ещё раз») | (Истёк — код запрашивается заново).
/// После получения токена: uid/displayName из account/status (необязательно),
/// YmService.SaveSessionOAuth(token, uid, displayName), DialogResult=true.
/// Токен в лог не пишется (логируются только uid/факт сохранения).
/// </summary>
public partial class YmLoginWindow : Window
{
    private readonly YmService _ym;
    private readonly DispatcherTimer _pollTimer;
    private readonly CancellationTokenSource _cts = new();

    private YmDeviceCode? _deviceCode;
    private int _pollIntervalSeconds = YmService.DefaultPollIntervalSeconds;
    private bool _finished;

    public YmLoginWindow(YmService ym)
    {
        InitializeComponent();
        _ym = ym;

        _pollTimer = new DispatcherTimer();
        _pollTimer.Tick += async (_, _) => await PollTokenTickAsync();

        Loaded += async (_, _) => await RequestNewCodeAsync();
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _pollTimer.Stop();
        };
    }

    // ======================== Код устройства ========================

    /// <summary>Запросить новый код и начать опрос (старт окна, повтор по «Ещё раз»,
    /// авто-обновление после истечения кода).</summary>
    private async Task RequestNewCodeAsync()
    {
        if (_finished) return;

        SetBusy(requesting: true);
        try
        {
            var code = await _ym.RequestDeviceCodeAsync(_cts.Token);
            _deviceCode = code;
            _pollIntervalSeconds = code.IntervalSeconds > 0
                ? code.IntervalSeconds
                : YmService.DefaultPollIntervalSeconds;

            UserCodeText.Text = code.UserCode;
            StatusText.Text = Loc.Get("YmDeviceWaiting");
            StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                "TextSecondaryBrush");

            SetBusy(requesting: false);

            _pollTimer.Interval = TimeSpan.FromSeconds(_pollIntervalSeconds);
            _pollTimer.Start();
        }
        catch (OperationCanceledException)
        {
            // окно закрыли во время запроса
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Yandex Music login: device code request failed");
            ShowFailure(Loc.Get("YmDeviceFailed"));
        }
    }

    // ========================= Опрос токена =========================

    private async Task PollTokenTickAsync()
    {
        if (_finished || _deviceCode == null) return;

        _pollTimer.Stop();
        try
        {
            var result = await _ym.PollDeviceTokenAsync(_deviceCode.DeviceCode, _cts.Token);

            if (!string.IsNullOrEmpty(result.AccessToken))
            {
                await FinishAsync(result.AccessToken);
                return;
            }

            if (result.IsPending)
            {
                _pollTimer.Start(); // следующий тик по прежнему интервалу
            }
            else if (result.IsSlowDown)
            {
                // Яндекс попросил опрашивать реже (интервал + 5 c).
                _pollIntervalSeconds += 5;
                _pollTimer.Interval = TimeSpan.FromSeconds(_pollIntervalSeconds);
                _pollTimer.Start();
            }
            else if (result.IsExpired)
            {
                // Код истёк — тихо запрашиваем новый, пользователь вводит его заново.
                Logger.Info("Yandex Music login: device code expired — requesting a new one");
                await RequestNewCodeAsync();
            }
            else if (result.IsDenied)
            {
                Logger.Warn($"Yandex Music login: device token denied ({result.ErrorCode})");
                ShowFailure(Loc.Get("YmAuthDenied"));
            }
            else
            {
                // invalid_response / неизвестный error — сетевой мусор или изменённая
                // раскладка: пробуем снова по обычному тику.
                Logger.Warn($"Yandex Music login: device token poll issue ({result.ErrorCode})");
                _pollTimer.Start();
            }
        }
        catch (OperationCanceledException)
        {
            // окно закрыли во время опроса
        }
        catch (Exception ex)
        {
            // Разовый сетевой сбой не роняет вход — следующий тик повторит.
            Logger.Error(ex, "Yandex Music login: token poll failed");
            _pollTimer.Start();
        }
    }

    // ========================= Завершение ===========================

    /// <summary>Сохранение OAuth-токена и закрытие окна (идемпотентно).</summary>
    private async Task FinishAsync(string accessToken)
    {
        if (_finished) return;
        _finished = true;
        _pollTimer.Stop();

        StatusText.Text = Loc.Get("YmDeviceConfirmed");

        // uid/displayName — не обязательно: не получили, синк дозаполнит первым запросом.
        var account = await ResolveAccountAsync(accessToken);

        _ym.SaveSessionOAuth(accessToken, account?.Uid, account?.DisplayName);

        DialogResult = true;
        Close();
    }

    /// <summary>uid/displayName из account/status. null — не получилось (токен всё равно
    /// сохраняется).</summary>
    private async Task<YmAccountInfo?> ResolveAccountAsync(string accessToken)
    {
        try
        {
            var account = await YmService.FetchAccountInfoAsync(accessToken, CancellationToken.None);
            if (account != null)
                Logger.Info($"Yandex Music login: account uid {account.Uid} resolved");
            else
                Logger.Warn("Yandex Music login: account uid could not be resolved (token saved without uid)");
            return account;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Yandex Music login: account status request failed");
            return null;
        }
    }

    // =========================== UI =================================

    /// <summary>Запрос кода идёт: кнопки неактивны, статус «получаем код».</summary>
    private void SetBusy(bool requesting)
    {
        OpenPageButton.IsEnabled = !requesting;
        RetryButton.Visibility = Visibility.Collapsed;
        if (requesting)
        {
            _pollTimer.Stop();
            UserCodeText.Text = "— — — —";
            StatusText.Text = Loc.Get("YmDeviceRequesting");
        }
    }

    /// <summary>Ошибка входа: статус + кнопка «Ещё раз» (новый код).</summary>
    private void ShowFailure(string message)
    {
        _pollTimer.Stop();
        StatusText.Text = message;
        StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            "AccentBrush");
        RetryButton.Visibility = Visibility.Visible;
    }

    private void OpenPageButton_Click(object sender, RoutedEventArgs e)
    {
        // verification_url из ответа Яндекса; пустой — дефолтная страница device.
        var url = !string.IsNullOrEmpty(_deviceCode?.VerificationUrl)
            ? _deviceCode.VerificationUrl
            : YmService.DefaultVerificationUrl;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Yandex Music login: failed to open verification page");
            StatusText.Text = YmService.DefaultVerificationUrl;
        }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
        => await RequestNewCodeAsync();

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            try { DragMove(); } catch { /* окно могло закрыться */ }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
