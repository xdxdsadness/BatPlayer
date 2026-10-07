using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BatPlayer.Audio;
using BatPlayer.Database;
using BatPlayer.Localization;
using BatPlayer.Models;
using BatPlayer.Services;
using BatPlayer.Services.SoundCloud;
using BatPlayer.Services.Vk;
using BatPlayer.Services.YandexMusic;
using BatPlayer.Services.Spotify;

namespace BatPlayer.ViewModels;

public partial class SettingsViewModel : PageViewModel
{
    private readonly SettingsService _settings;
    private readonly LibraryService _library;
    private readonly AudioService _audio;
    private readonly SoundCloudService _soundCloud;
    private readonly SoundCloudLoginService _soundCloudLogin;
    private readonly Services.SoundCloud.SoundCloudOfficialAuth _scApiAuth;
    private readonly SoundCloudLikesRepository _soundCloudLikes;
    private readonly VkService _vk;
    private readonly VkLoginService _vkLogin;
    private readonly VkTracksRepository _vkTracks;
    private readonly YmService _ym;
    private readonly YmLoginService _ymLogin;
    private readonly YmTracksRepository _ymTracks;
    private readonly SpotifyService _spotify;
    private readonly SpotifyTracksRepository _spotifyTracks;

    public ObservableCollection<string> AudioDevices { get; } = new();
    public ObservableCollection<string> LibraryFolders { get; } = new();

    [ObservableProperty] private string _selectedAudioDevice = string.Empty;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _autoResumePlayback;
    [ObservableProperty] private bool _confirmDeletion;
    [ObservableProperty] private bool _useWasapiExclusive;
    [ObservableProperty] private bool _gaplessPlayback;
    [ObservableProperty] private bool _crossfadeEnabled;
    [ObservableProperty] private int _crossfadeDurationMs = 3000;
    [ObservableProperty] private bool _replayGainEnabled;
    [ObservableProperty] private bool _normalizeVolume;
    [ObservableProperty] private bool _smoothVolumeChanges = true;
    [ObservableProperty] private bool _sidebarVisible = true;
    [ObservableProperty] private bool _animationsEnabled = true;
    [ObservableProperty] private string _selectedLanguage = "en";
    [ObservableProperty] private string _selectedThemeMode = "standard";
    [ObservableProperty] private string _selectedAccentColor = "default";
    [ObservableProperty] private int _selectedGridColumns = 4;
    [ObservableProperty] private bool _coverShadows;
    [ObservableProperty] private bool _backgroundGifEnabled;
    [ObservableProperty] private string _backgroundGifPath = string.Empty;
    [ObservableProperty] private int _backgroundGifOpacity = 30;
    [ObservableProperty] private bool _backgroundBlurEnabled;
    [ObservableProperty] private int _backgroundBlurRadius = 24;
    [ObservableProperty] private bool _scanOnStartup;
    [ObservableProperty] private bool _useGlobalHotkeys = true;

    // SoundCloud
    [ObservableProperty] private string _soundCloudStatusText = string.Empty;
    [ObservableProperty] private bool _isSoundCloudConnected;
    [ObservableProperty] private bool _isSoundCloudBusy;
    [ObservableProperty] private string _soundCloudProxy = string.Empty;

    // VK
    [ObservableProperty] private string _vkStatusText = string.Empty;
    [ObservableProperty] private bool _isVkConnected;
    [ObservableProperty] private bool _isVkBusy;

    // Yandex Music
    [ObservableProperty] private string _ymStatusText = string.Empty;
    [ObservableProperty] private bool _isYmConnected;
    [ObservableProperty] private bool _isYmBusy;

    // Spotify
    [ObservableProperty] private string _spotifyStatusText = string.Empty;
    [ObservableProperty] private bool _isSpotifyConnected;
    [ObservableProperty] private bool _isSpotifyBusy;

    /// <summary>Errors from SoundCloud actions in settings — main window toast.</summary>
    public event EventHandler<string>? ErrorOccurred;

    public SettingsViewModel(SettingsService settings, LibraryService library, AudioService audio,
                             SoundCloudService soundCloud, SoundCloudLoginService soundCloudLogin,
                             SoundCloudLikesRepository soundCloudLikes,
                             Services.SoundCloud.SoundCloudOfficialAuth scApiAuth,
                             VkService vk, VkLoginService vkLogin, VkTracksRepository vkTracks,
                             YmService ym, YmLoginService ymLogin, YmTracksRepository ymTracks,
                             SpotifyService spotify, SpotifyTracksRepository spotifyTracks)
    {
        _settings = settings;
        _library = library;
        _audio = audio;
        _soundCloud = soundCloud;
        _soundCloudLogin = soundCloudLogin;
        _scApiAuth = scApiAuth;
        _soundCloudLikes = soundCloudLikes;
        _vk = vk;
        _vkLogin = vkLogin;
        _vkTracks = vkTracks;
        _ym = ym;
        _ymLogin = ymLogin;
        _ymTracks = ymTracks;
        _spotify = spotify;
        _spotifyTracks = spotifyTracks;
        Title = Loc.Get("Settings");
        ScApiConnected = _scApiAuth.IsConnected;
        ScApiStatusText = ScApiConnected ? "SoundCloud API: OK" : Loc.Get("NotConnected");

        // VM lives as long as the app; on language change we refresh
        // the title and the "(System default)" label in the device list.
        Loc.LanguageChanged += (_, _) =>
        {
            Title = Loc.Get("Settings");
            _ = LoadAudioDevicesAsync();
            _ = RefreshSoundCloudStatusAsync();
            _ = RefreshVkStatusAsync();
            _ = RefreshYmStatusAsync();
            _ = RefreshSpotifyStatusAsync();
        };

        LoadFromSettings();
        _ = LoadAudioDevicesAsync();
        _ = LoadFoldersAsync();
        _ = RefreshSoundCloudStatusAsync();
        _ = RefreshVkStatusAsync();
        _ = RefreshYmStatusAsync();
        _ = RefreshSpotifyStatusAsync();
    }

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        StartWithWindows      = s.StartWithWindows;
        MinimizeToTray        = s.MinimizeToTray;
        CloseToTray           = s.CloseToTray;
        AutoResumePlayback    = s.AutoResumePlayback;
        ConfirmDeletion       = s.ConfirmDeletion;
        UseWasapiExclusive    = s.UseWasapiExclusive;
        GaplessPlayback       = s.GaplessPlayback;
        CrossfadeEnabled      = s.CrossfadeEnabled;
        CrossfadeDurationMs   = s.CrossfadeDurationMs;
        ReplayGainEnabled     = s.ReplayGainEnabled;
        NormalizeVolume       = s.NormalizeVolume;
        SmoothVolumeChanges   = s.SmoothVolumeChanges;
        SidebarVisible        = s.SidebarVisible;
        AnimationsEnabled     = s.AnimationsEnabled;
        SelectedLanguage      = s.Language;
        ScanOnStartup         = s.ScanOnStartup;
        UseGlobalHotkeys      = s.UseGlobalHotkeys;
        SelectedAudioDevice   = s.AudioOutputDevice;
        SoundCloudProxy       = s.SoundCloudProxy ?? string.Empty;
        SelectedThemeMode     = s.ThemeMode;
        SelectedAccentColor   = s.AccentColor;
        SelectedGridColumns   = s.GridColumns;
        CoverShadows          = s.CoverShadows;
        BackgroundGifEnabled  = s.BackgroundGifEnabled;
        BackgroundGifPath     = s.BackgroundGifPath ?? string.Empty;
        BackgroundGifOpacity  = s.BackgroundGifOpacity;
        BackgroundBlurEnabled = s.BackgroundBlurEnabled;
        BackgroundBlurRadius = s.BackgroundBlurRadius;
    }

    private async Task LoadAudioDevicesAsync()
    {
        AudioDevices.Clear();
        AudioDevices.Add(Loc.Get("DefaultDevice"));
        foreach (var d in _audio.EnumerateOutputDevices())
            AudioDevices.Add(d);
        SelectedAudioDevice = _settings.Current.AudioOutputDevice;
    }

    private async Task LoadFoldersAsync()
    {
        var folders = await _library.GetLibraryFoldersAsync();
        LibraryFolders.Clear();
        foreach (var f in folders) LibraryFolders.Add(f.Path);
    }

    partial void OnSelectedAudioDeviceChanged(string value)
        => _settings.Update(s => s.AudioOutputDevice = value);

    partial void OnUseWasapiExclusiveChanged(bool value)
        => _settings.Update(s => s.UseWasapiExclusive = value);

    partial void OnGaplessPlaybackChanged(bool value)
        => _settings.Update(s => s.GaplessPlayback = value);

    partial void OnCrossfadeEnabledChanged(bool value)
        => _settings.Update(s => s.CrossfadeEnabled = value);

    partial void OnCrossfadeDurationMsChanged(int value)
        => _settings.Update(s => s.CrossfadeDurationMs = value);

    partial void OnReplayGainEnabledChanged(bool value)
        => _settings.Update(s => s.ReplayGainEnabled = value);

    partial void OnNormalizeVolumeChanged(bool value)
    {
        _settings.Update(s => s.NormalizeVolume = value);
        // Live apply: the current track's gain is recomputed (or reset to 1).
        _audio.SetNormalizationEnabled(value);
    }

    partial void OnSmoothVolumeChangesChanged(bool value)
        => _settings.Update(s => s.SmoothVolumeChanges = value);

    partial void OnAutoResumePlaybackChanged(bool value)
        => _settings.Update(s => s.AutoResumePlayback = value);

    partial void OnConfirmDeletionChanged(bool value)
        => _settings.Update(s => s.ConfirmDeletion = value);

    partial void OnMinimizeToTrayChanged(bool value)
        => _settings.Update(s => s.MinimizeToTray = value);

    partial void OnCloseToTrayChanged(bool value)
        => _settings.Update(s => s.CloseToTray = value);

    partial void OnSidebarVisibleChanged(bool value)
        => _settings.Update(s => s.SidebarVisible = value);

    partial void OnAnimationsEnabledChanged(bool value)
        => _settings.Update(s => s.AnimationsEnabled = value);

    partial void OnSelectedThemeModeChanged(string value)
        => _settings.Update(s => s.ThemeMode = value);
    partial void OnSelectedAccentColorChanged(string value)
        => _settings.Update(s => s.AccentColor = value);
    partial void OnSelectedGridColumnsChanged(int value)
        => _settings.Update(s => s.GridColumns = value);
    partial void OnCoverShadowsChanged(bool value)
        => _settings.Update(s => s.CoverShadows = value);
    partial void OnBackgroundBlurEnabledChanged(bool value)
        => _settings.Update(s => s.BackgroundBlurEnabled = value);
    partial void OnBackgroundBlurRadiusChanged(int value)
        => _settings.Update(s => s.BackgroundBlurRadius = value);
    partial void OnBackgroundGifEnabledChanged(bool value)
        => _settings.Update(s => s.BackgroundGifEnabled = value);
    partial void OnBackgroundGifPathChanged(string value)
        => _settings.Update(s => s.BackgroundGifPath = value ?? string.Empty);
    partial void OnBackgroundGifOpacityChanged(int value)
        => _settings.Update(s => s.BackgroundGifOpacity = value);

    [RelayCommand]
    private void PickBackgroundGif()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "GIF|*.gif|All files|*.*",
            Title = Loc.Get("BackgroundSelect")
        };
        if (dlg.ShowDialog() == true)
        {
            BackgroundGifPath = dlg.FileName;
            BackgroundGifEnabled = true;
        }
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        // Save to settings.json and apply the language on the fly (all Loc.T bindings re-read).
        _settings.Update(s => s.Language = value);
        Loc.SetLanguage(value);
    }

    partial void OnScanOnStartupChanged(bool value)
        => _settings.Update(s => s.ScanOnStartup = value);

    partial void OnUseGlobalHotkeysChanged(bool value)
        => _settings.Update(s => s.UseGlobalHotkeys = value);

    partial void OnSoundCloudProxyChanged(string value)
        // The SoundCloud network layer picks up the new value via SettingsService.SettingsChanged.
        => _settings.Update(s => s.SoundCloudProxy = value?.Trim() ?? string.Empty);

    [RelayCommand]
    private async Task AddLibraryFolderAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog() != true) return;
        await _library.AddLibraryFolderAsync(dlg.FolderName);
        await LoadFoldersAsync();
        // Scan the new folder right away: otherwise tracks appear only after
        // "Scan now" or a restart, and "Add folder" looks broken.
        // (The Add folder button on the main panel scans immediately — same here.)
        await _library.ScanFolderAsync(dlg.FolderName);
    }

    [RelayCommand]
    private async Task RemoveLibraryFolderAsync(string path)
    {
        await _library.RemoveLibraryFolderAsync(path);
        await LoadFoldersAsync();
    }

    [RelayCommand]
    private async Task ScanNowAsync()
    {
        var folders = LibraryFolders.ToList();
        await _library.ScanAllConfiguredFoldersAsync(folders);
    }

    [RelayCommand]
    private async Task ClearCoverCacheAsync()
    {
        // CoverCacheService is reached via App.Services
        (App.Services.GetService(typeof(CoverCacheService)) as CoverCacheService)?.Clear();
    }

    // ============================ SoundCloud ============================

    /// <summary>Connection status: NotConnected / ConnectedAs &lt;username&gt; (network check via /me).</summary>
    private async Task RefreshSoundCloudStatusAsync()
    {
        try
        {
            if (!_soundCloud.HasAuthFile)
            {
                IsSoundCloudConnected = false;
                SoundCloudStatusText = Loc.Get("NotConnected");
                return;
            }

            // Cookies present — validate them against /me (15s timeout inside the service).
            var me = await _soundCloud.GetMeAsync(CancellationToken.None);
            IsSoundCloudConnected = me != null;
            SoundCloudStatusText = me != null
                ? $"{Loc.Get("ConnectedAs")} {me.Username}"
                : Loc.Get("SoundCloudSessionExpired");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud status check failed");
        }
    }

    // ===================== Official SoundCloud API (PKCE + local callback) =====================

    private bool _scApiConnected;
    public bool ScApiConnected
    {
        get => _scApiConnected;
        set { _scApiConnected = value; OnPropertyChanged(); }
    }

    private string _scApiStatusText = string.Empty;
    public string ScApiStatusText
    {
        get => _scApiStatusText;
        set { _scApiStatusText = value; OnPropertyChanged(); }
    }

    private bool _scApiConnecting;
    private bool _scApiManualMode;

    private string _scApiManualClientId = string.Empty;
    public string ScApiManualClientId
    {
        get => _scApiManualClientId;
        set { _scApiManualClientId = value; OnPropertyChanged(); }
    }

    public bool ScApiManualVisible => _scApiManualMode;

    /// <summary>Connect the official API: browser (PKCE) → local callback
    /// on 127.0.0.1:8765 → token → app auto-registration. Statuses go to the text.</summary>
    [RelayCommand]
    private async Task ConnectScApiAsync()
    {
        if (ScApiConnected || _scApiConnecting) return;
        _scApiConnecting = true;
        ((System.Windows.Input.ICommand)ConnectScApiCommand).CanExecuteChanged += (_, _) => { };
        try
        {
            ScApiStatusText = Loc.Get("ScApiOpeningBrowser");
            var file = await _scApiAuth.ConnectLocallyAsync(
                status => ScApiStatusText = status,
                CancellationToken.None);

            ScApiConnected = !string.IsNullOrEmpty(file.AccessToken);
            if (ScApiConnected && string.IsNullOrEmpty(file.ClientSecret))
            {
                // The token came from the bundled client, but registering the USER'S OWN app
                // failed (e.g. Artist Pro subscription needed — the reason is already in the
                // status). The bundled SoundCloud client may be blocked (403 disallowed on
                // streams), so we immediately offer manual entry of the app's client_id.
                _scApiManualMode = true;
                OnPropertyChanged(nameof(ScApiManualVisible));
                ScApiStatusText += Loc.Get("ScApiManualHint");
            }
            else
            {
                ScApiStatusText = ScApiConnected ? "SoundCloud API: OK" : Loc.Get("NotConnected");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SC API local pairing failed");
            var isReg = ex.Message.Contains("App registration failed");
            ScApiStatusText = isReg
                ? Loc.Get("ScApiAutoRegUnavailable")
                : Loc.Get("ErrorPrefix") + ex.Message;
            _scApiManualMode = isReg;
        }
        finally
        {
            _scApiConnecting = false;
            OnPropertyChanged(nameof(ScApiManualVisible));
        }
    }

    /// <summary>Manual mode: PKCE with a user-provided client_id (the app was created
    /// on soundcloud.com/you/apps; redirect: http://127.0.0.1:8765/callback).</summary>
    [RelayCommand]
    private async Task SaveScApiClientIdAsync()
    {
        var clientId = ScApiManualClientId.Trim();
        if (clientId.Length < 8) { ScApiStatusText = Loc.Get("ClientIdTooShort"); return; }
        _scApiConnecting = true;
        try
        {
            ScApiStatusText = Loc.Get("ScApiOpeningBrowser");
            var file = await _scApiAuth.ConnectWithClientIdAsync(clientId, null,
                status => ScApiStatusText = status, CancellationToken.None);
            ScApiConnected = !string.IsNullOrEmpty(file.AccessToken);
            ScApiStatusText = ScApiConnected ? "SoundCloud API: OK" : Loc.Get("NotConnected");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SC API manual connect failed");
            ScApiStatusText = Loc.Get("ErrorPrefix") + ex.Message;
        }
        finally
        {
            _scApiConnecting = false;
            OnPropertyChanged(nameof(ScApiManualVisible));
        }
    }

    [RelayCommand]
    private void DisconnectScApi()
    {
        _scApiAuth.Disconnect();
        ScApiConnected = false;
        ScApiStatusText = Loc.Get("NotConnected");
    }

    /// <summary>Connect an account: WebView2 login window; on success — refresh the status.
    /// The WebView2 profile is NOT cleared: cookies persist — autologin returns to the
    /// same account without a password (a clean login is via "Switch account").</summary>
    [RelayCommand]
    private async Task ConnectSoundCloudAsync()
    {
        try
        {
            var owner = System.Windows.Application.Current?.Windows
                .OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive);
            if (await _soundCloudLogin.LoginAsync(owner!))
                await RefreshSoundCloudStatusAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud connect failed");
            ErrorOccurred?.Invoke(this, Loc.Get("SoundCloudSyncFailed"));
        }
    }

    /// <summary>
    /// Switch account: opens the ORIGINAL SoundCloud web window, where the user signs out
    /// of the current session and logs in with any account they want (the site remembers
    /// the email and offers saved options — like a Google account chooser, via SC itself).
    /// As soon as a new session (oauth_token) appears on the site, the window closes,
    /// the app picks it up, and the previous account's likes are cleared for a re-sync.
    /// </summary>
    [RelayCommand]
    private async Task SwitchSoundCloudAccountAsync()
    {
        var owner = System.Windows.Application.Current?.Windows
            .OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive);
        IsSoundCloudBusy = true;
        try
        {
            if (await _soundCloudLogin.LoginAsync(owner!, signOutFirst: true))
            {
                // The session changed (or was re-confirmed) — the previous account's
                // likes no longer match; clear them for the new account's sync.
                await _soundCloudLikes.ClearAllAsync();
                await RefreshSoundCloudStatusAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud account switch failed");
            ErrorOccurred?.Invoke(this, Loc.Get("SoundCloudSyncFailed"));
        }
        finally
        {
            IsSoundCloudBusy = false;
        }
    }

    [RelayCommand]
    private async Task SyncSoundCloudAsync()
    {
        if (IsSoundCloudBusy) return;
        if (!_soundCloud.HasAuthFile)
        {
            ErrorOccurred?.Invoke(this, Loc.Get("NotConnected"));
            return;
        }

        IsSoundCloudBusy = true;
        try
        {
            await _soundCloud.SyncLikesAsync(_soundCloudLikes, CancellationToken.None);
        }
        catch (SoundCloudApiException ex)
        {
            Logger.Error($"SoundCloud sync failed: HTTP {ex.StatusCode}");
            ErrorOccurred?.Invoke(this, $"{Loc.Get("SoundCloudSyncFailed")}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud sync failed");
            ErrorOccurred?.Invoke(this, Loc.Get("SoundCloudSyncFailed"));
        }
        finally
        {
            IsSoundCloudBusy = false;
        }
    }

    /// <summary>Disconnect: delete sc_auth.json and clear the likes table.</summary>
    [RelayCommand]
    private async Task DisconnectSoundCloudAsync()
    {
        IsSoundCloudBusy = true;
        try
        {
            _soundCloud.Disconnect();
            await _soundCloudLikes.ClearAllAsync();
            await RefreshSoundCloudStatusAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SoundCloud disconnect failed");
        }
        finally
        {
            IsSoundCloudBusy = false;
        }
    }

    // =============================== VK ==================================

    /// <summary>VK connection status: not connected / ConnectedAs id&lt;uid&gt; (based on the
    /// presence of web-session cookies in vk_auth.json, no network check — an expired
    /// session will surface on the first catalog request and be reset).</summary>
    private async Task RefreshVkStatusAsync()
    {
        try
        {
            if (!_vk.HasWebSession)
            {
                IsVkConnected = false;
                VkStatusText = Loc.Get("NotConnected");
                return;
            }

            // Session cookies present. The username is not available from cookies (users.get
            // required a dead token) — show the id, if it was extracted at login.
            var userId = _vk.GetUserId();
            IsVkConnected = true;
            VkStatusText = userId.Length > 0
                ? $"{Loc.Get("ConnectedAs")} id{userId}"
                : Loc.Get("VkConnected");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VK status check failed");
        }
    }

    /// <summary>Connect a VK account: WebView2 login window (web-session cookies); on success —
    /// refresh the status.</summary>
    [RelayCommand]
    private async Task ConnectVkAsync()
    {
        try
        {
            var owner = System.Windows.Application.Current?.Windows
                .OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive);
            if (await _vkLogin.LoginAsync(owner!))
            {
                await RefreshVkStatusAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VK connect failed");
            ErrorOccurred?.Invoke(this, Loc.Get("VkSyncFailed"));
        }
    }

    [RelayCommand]
    private async Task SyncVkAsync()
    {
        if (IsVkBusy) return;
        if (!_vk.HasWebSession)
        {
            ErrorOccurred?.Invoke(this, Loc.Get("NotConnected"));
            return;
        }

        IsVkBusy = true;
        try
        {
            await _vk.SyncAudioAsync(_vkTracks, CancellationToken.None);
        }
        catch (VkApiException ex)
        {
            Logger.Error($"VK sync failed (error code {ex.ErrorCode})");
            if (ex.ErrorCode == 5)
            {
                // Cookies invalidated (VK returned the login page): the service has already
                // cleared the cookie string — status "not connected", the Connect button lets the user log in again.
                ErrorOccurred?.Invoke(this, Loc.Get("VkSessionExpired"));
            }
            else if (VkService.IsAudioPermissionError(ex.ErrorCode))
                ErrorOccurred?.Invoke(this, Loc.Get("VkAudioPermissionDenied"));
            else
                ErrorOccurred?.Invoke(this, $"{Loc.Get("VkSyncFailed")}: {ex.Message}");
            await RefreshVkStatusAsync(); // code 5 reset the session — status "not connected"
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VK sync failed");
            ErrorOccurred?.Invoke(this, Loc.Get("VkSyncFailed"));
        }
        finally
        {
            IsVkBusy = false;
        }
    }

    /// <summary>Disconnect: delete vk_auth.json and clear the vk_tracks table.</summary>
    [RelayCommand]
    private async Task DisconnectVkAsync()
    {
        IsVkBusy = true;
        try
        {
            _vk.Disconnect();
            await _vkTracks.ClearAllAsync();
            await RefreshVkStatusAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "VK disconnect failed");
        }
        finally
        {
            IsVkBusy = false;
        }
    }

    // ========================== Yandex Music =============================

    /// <summary>Yandex Music connection status: not connected / ConnectedAs &lt;name&gt;
    /// (based on the OAuth token in ym_auth.json; a network check via account/status pulls
    /// fresh name/uid — if it fails (network/revoked token), the values saved at login
    /// are shown; the token will be rejected on the first catalog request).</summary>
    private async Task RefreshYmStatusAsync()
    {
        try
        {
            if (!_ym.HasToken)
            {
                IsYmConnected = false;
                YmStatusText = Loc.Get("YmNotConnected");
                return;
            }

            // Token present: fresh name/uid come from account/status; fallback — the values
            // saved at login (GetAccountStatusAsync does not throw — returns null).
            IsYmConnected = true;
            var name = _ym.GetSavedDisplayName();
            var uid = _ym.GetSavedUid();
            var account = await _ym.GetAccountStatusAsync(CancellationToken.None);
            if (account != null)
            {
                if (account.DisplayName.Length > 0) name = account.DisplayName;
                if (account.Uid.Length > 0) uid = account.Uid;
            }
            YmStatusText = name.Length > 0 ? $"{Loc.Get("YmConnectedAs")} {name}"
                         : uid.Length > 0 ? $"{Loc.Get("YmConnectedAs")} {uid}"
                         : Loc.Get("YmConnected");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Yandex Music status check failed");
        }
    }

    /// <summary>Connect a Yandex Music account: WebView2 login window (Yandex ID OAuth);
    /// on success — refresh the status.</summary>
    [RelayCommand]
    private async Task ConnectYmAsync()
    {
        try
        {
            var owner = System.Windows.Application.Current?.Windows
                .OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive);
            if (await _ymLogin.LoginAsync(owner!))
            {
                await RefreshYmStatusAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Yandex Music connect failed");
            ErrorOccurred?.Invoke(this, Loc.Get("YmSyncFailed"));
        }
    }

    [RelayCommand]
    private async Task SyncYmAsync()
    {
        if (IsYmBusy) return;
        if (!_ym.HasToken)
        {
            ErrorOccurred?.Invoke(this, Loc.Get("YmNotConnected"));
            return;
        }

        IsYmBusy = true;
        try
        {
            await _ym.SyncLikedTracksAsync(_ymTracks, CancellationToken.None);
        }
        catch (YmApiException ex)
        {
            Logger.Error($"Yandex Music sync failed (HTTP {ex.HttpCode})");
            if (YmApiException.IsSessionError(ex.HttpCode))
            {
                // Token revoked (API returned 401/403): the service has already cleared
                // AccessToken — status "not connected", the Connect button lets the user log in again.
                ErrorOccurred?.Invoke(this, Loc.Get("YmSessionExpired"));
            }
            else
                ErrorOccurred?.Invoke(this, $"{Loc.Get("YmSyncFailed")}: {ex.Message}");
            await RefreshYmStatusAsync(); // 401/403 reset the session — status "not connected"
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Yandex Music sync failed");
            ErrorOccurred?.Invoke(this, Loc.Get("YmSyncFailed"));
        }
        finally
        {
            IsYmBusy = false;
        }
    }

    /// <summary>Disconnect: delete ym_auth.json and clear the ym_tracks table.</summary>
    [RelayCommand]
    private async Task DisconnectYmAsync()
    {
        IsYmBusy = true;
        try
        {
            _ym.Disconnect();
            await _ymTracks.ClearAllAsync();
            await RefreshYmStatusAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Yandex Music disconnect failed");
        }
        finally
        {
            IsYmBusy = false;
        }
    }

    // ============================= Spotify ===============================

    /// <summary>Spotify connection status: not connected / ConnectedAs (based on the
    /// OAuth token in spotify_auth.json).</summary>
    private async Task RefreshSpotifyStatusAsync()
    {
        try
        {
            if (!_spotify.HasAuthFile)
            {
                IsSpotifyConnected = false;
                SpotifyStatusText = Loc.Get("NotConnected");
                return;
            }

            IsSpotifyConnected = true;
            var userId = _spotify.GetUserId();
            var displayName = _spotify.GetDisplayName();
            
            SpotifyStatusText = displayName.Length > 0 
                ? $"{Loc.Get("ConnectedAs")} {displayName}"
                : userId.Length > 0 
                    ? $"{Loc.Get("ConnectedAs")} {userId}"
                    : Loc.Get("SpotifyConnected");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify status check failed");
        }
    }

    /// <summary>Connect a Spotify account: OAuth authorization window; on success — refresh the status.</summary>
    [RelayCommand]
    private async Task ConnectSpotifyAsync()
    {
        try
        {
            var owner = System.Windows.Application.Current?.Windows
                .OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive);
            
            var loginWindow = new Views.SpotifyLoginWindow(_spotify) { Owner = owner };
            var result = loginWindow.ShowDialog();
            
            if (result == true)
            {
                await RefreshSpotifyStatusAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify connect failed");
            ErrorOccurred?.Invoke(this, "Spotify connection failed");
        }
    }

    [RelayCommand]
    private async Task SyncSpotifyAsync()
    {
        if (IsSpotifyBusy) return;
        if (!_spotify.HasAuthFile)
        {
            ErrorOccurred?.Invoke(this, Loc.Get("NotConnected"));
            return;
        }

        IsSpotifyBusy = true;
        try
        {
            await _spotify.SyncSavedTracksAsync(_spotifyTracks, CancellationToken.None);
        }
        catch (SpotifyApiException ex)
        {
            Logger.Error($"Spotify sync failed (HTTP {ex.StatusCode})");
            if (ex.StatusCode == 401 || ex.StatusCode == 403)
            {
                // Token revoked: clear the session
                ErrorOccurred?.Invoke(this, "Spotify session expired");
            }
            else
                ErrorOccurred?.Invoke(this, $"Spotify sync failed: {ex.Message}");
            await RefreshSpotifyStatusAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify sync failed");
            ErrorOccurred?.Invoke(this, "Spotify sync failed");
        }
        finally
        {
            IsSpotifyBusy = false;
        }
    }

    /// <summary>Disconnect: delete spotify_auth.json and clear the spotify_tracks table.</summary>
    [RelayCommand]
    private async Task DisconnectSpotifyAsync()
    {
        IsSpotifyBusy = true;
        try
        {
            _spotify.Disconnect();
            await _spotifyTracks.DeleteAllAsync();
            await RefreshSpotifyStatusAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify disconnect failed");
        }
        finally
        {
            IsSpotifyBusy = false;
        }
    }
}
