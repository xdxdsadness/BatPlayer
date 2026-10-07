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
/// Yandex Music login window — Yandex ID OAuth Device Flow (no WebView2).
///
/// Why not an implicit redirect: the Yandex Music client's consent on oauth.yandex.ru only
/// works with the official Android app client_id, and the token arrives at music.yandex.ru,
/// which instantly redirects further (the fragment is lost) — a fragile scheme. Device Flow
/// is reliable: the player requests a code (POST device/code), shows it to the user, who
/// enters it on the official oauth.yandex.ru/device page in a browser, while the player
/// polls POST token (grant_type=device_code) at the recommended interval until the code is
/// confirmed or expires.
///
/// States: RequestCode → Waiting → (Confirmed — window closes) |
/// (Denied/Network — status + "Try again") | (Expired — a new code is requested).
/// After the token is received: uid/displayName from account/status (optional),
/// YmService.SaveSessionOAuth(token, uid, displayName), DialogResult=true.
/// The token is never logged (only the uid and the fact of saving are logged).
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

    // ======================== Device code ========================

    /// <summary>Request a new code and start polling (window start, retry via "Try again",
    /// auto-refresh after the code expires).</summary>
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
            // window closed during the request
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Yandex Music login: device code request failed");
            ShowFailure(Loc.Get("YmDeviceFailed"));
        }
    }

    // ========================= Token polling =========================

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
                _pollTimer.Start(); // next tick at the same interval
            }
            else if (result.IsSlowDown)
            {
                // Yandex asked to poll less often (interval + 5s).
                _pollIntervalSeconds += 5;
                _pollTimer.Interval = TimeSpan.FromSeconds(_pollIntervalSeconds);
                _pollTimer.Start();
            }
            else if (result.IsExpired)
            {
                // Code expired — silently request a new one; the user enters it again.
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
                // invalid_response / unknown error — transient noise or a changed page
                // layout: retry on the next regular tick.
                Logger.Warn($"Yandex Music login: device token poll issue ({result.ErrorCode})");
                _pollTimer.Start();
            }
        }
        catch (OperationCanceledException)
        {
            // window closed during polling
        }
        catch (Exception ex)
        {
            // A one-off network failure doesn't kill the login — the next tick retries.
            Logger.Error(ex, "Yandex Music login: token poll failed");
            _pollTimer.Start();
        }
    }

    // ========================= Finish ===========================

    /// <summary>Save the OAuth token and close the window (idempotent).</summary>
    private async Task FinishAsync(string accessToken)
    {
        if (_finished) return;
        _finished = true;
        _pollTimer.Stop();

        StatusText.Text = Loc.Get("YmDeviceConfirmed");

        // uid/displayName are optional: if not resolved, catalog sync fills them in on the first request.
        var account = await ResolveAccountAsync(accessToken);

        _ym.SaveSessionOAuth(accessToken, account?.Uid, account?.DisplayName);

        DialogResult = true;
        Close();
    }

    /// <summary>uid/displayName from account/status. null — not resolved (the token is saved anyway).</summary>
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

    /// <summary>Code request in progress: buttons disabled, status shows "requesting code".</summary>
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

    /// <summary>Login failure: status + "Try again" button (new code).</summary>
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
        // verification_url from Yandex's response; empty — default device page.
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
            try { DragMove(); } catch { /* window may already be closed */ }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
