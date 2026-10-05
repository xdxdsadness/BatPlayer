using System.Collections.Generic;

namespace BatPlayer.Models;

public sealed class AppSettings
{
    public string Language { get; set; } = "en";

    // General
    public bool StartWithWindows { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool AutoResumePlayback { get; set; } = true;
    public bool ConfirmDeletion { get; set; } = true;
    public bool UseHardwareAcceleration { get; set; } = true;

    // Library
    public List<string> LibraryFolders { get; set; } = new();
    public List<string> ExcludedFolders { get; set; } = new();
    public bool AutoScan { get; set; } = true;
    public bool ScanOnStartup { get; set; } = false;
    public bool UpdateMetadataOnScan { get; set; } = false;

    // SoundCloud
    // "" = авто (direct, при сетевом фейле — системный прокси WinINET);
    // "off" = только direct; "socks5://host:port" / "http://host:port" = явный прокси.
    public string SoundCloudProxy { get; set; } = string.Empty;

    /// <summary>Антиблокировка SoundCloud: при недоступности всех транспортов запустить
    /// встроенный zapret (winws, только домены SoundCloud). Требует UAC-подтверждения
    /// один раз за сессию; выключение полностью отключает механизм.</summary>
    public bool SoundCloudZapretEnabled { get; set; } = true;

    // Playback
    public string AudioOutputDevice { get; set; } = string.Empty; // empty = default
    public bool UseWasapiExclusive { get; set; } = false;
    public bool GaplessPlayback { get; set; } = false;
    public bool CrossfadeEnabled { get; set; } = false;
    public int CrossfadeDurationMs { get; set; } = 3000;
    public bool ReplayGainEnabled { get; set; } = false;
    public bool NormalizeVolume { get; set; } = false;
    public bool SmoothVolumeChanges { get; set; } = true;
    public bool EqualizerEnabled { get; set; } = false;
    public string CurrentEqualizerPreset { get; set; } = "Flat";
    // Пользовательские пресеты эквалайзера (хранит и наполняет EqualizerService)
    public List<EqualizerPreset> EqualizerUserPresets { get; set; } = new();

    // UI
    public double FontScale { get; set; } = 1.0;
    public double CoverSizeScale { get; set; } = 1.0;
    public string InterfaceDensity { get; set; } = "Comfortable"; // Comfortable / Compact
    public bool SidebarVisible { get; set; } = true;
    public bool SidebarCompact { get; set; } = false;
    public bool AnimationsEnabled { get; set; } = true;
    public bool FullscreenOnStartup { get; set; } = false;
    public bool PinMiniPlayer { get; set; } = false;

    // Theme & background customization
    public string ThemeMode { get; set; } = "standard";        // standard / darker / light
    public string AccentColor { get; set; } = "default";       // default / blue / green / purple / red / orange
    public int GridColumns { get; set; } = 4;                  // колонок карточек в ряду: 4 / 6 / 8
    public bool CoverShadows { get; set; } = false;            // мягкие тени под обложками

    // YouTube fallback: SC/VK/YM трек недоступен (DRM/policy/404) — ищем «исполнитель +
    public bool YouTubeFallbackEnabled { get; set; } = true;

    // Animated GIF background (heavy: decoded off UI thread, frame-by-frame)
    public bool BackgroundGifEnabled { get; set; } = false;
    public bool BackgroundBlurEnabled { get; set; } = false;   // блюр GIF/видео-фона
    public int BackgroundBlurRadius { get; set; } = 24;        // радиус размытия, px
    public string BackgroundGifPath { get; set; } = string.Empty;
    public int BackgroundGifOpacity { get; set; } = 30;        // % видимости фона поверх темы

    // Hotkeys (key codes as int, modifiers as flags)
    public HotkeyBinding PlayPause { get; set; } = new() { Key = 32, Modifiers = 0 };        // Space
    public HotkeyBinding NextTrack { get; set; } = new() { Key = 190, Modifiers = 0 };       // .
    public HotkeyBinding PreviousTrack { get; set; } = new() { Key = 188, Modifiers = 0 };   // ,
    public HotkeyBinding VolumeUp { get; set; } = new() { Key = 187, Modifiers = 0 };        // +
    public HotkeyBinding VolumeDown { get; set; } = new() { Key = 189, Modifiers = 0 };      // -
    public HotkeyBinding Mute { get; set; } = new() { Key = 77, Modifiers = 4 };             // Ctrl+M
    public HotkeyBinding Favorite { get; set; } = new() { Key = 66, Modifiers = 4 };         // Ctrl+B
    public HotkeyBinding ShowWindow { get; set; } = new() { Key = 80, Modifiers = 6 };       // Ctrl+Shift+P
    public HotkeyBinding Search { get; set; } = new() { Key = 70, Modifiers = 4 };           // Ctrl+F

    // Global hotkeys (Windows-wide, registered via RegisterHotKey)
    public bool UseGlobalHotkeys { get; set; } = true;
    public HotkeyBinding GlobalPlayPause { get; set; } = new() { Key = 80, Modifiers = 6 };    // Ctrl+Shift+P -> remapped to media
    public HotkeyBinding GlobalNext { get; set; } = new() { Key = 78, Modifiers = 6 };
    public HotkeyBinding GlobalPrev { get; set; } = new() { Key = 66, Modifiers = 6 };

    // Window
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; } = false;
}

public sealed class HotkeyBinding
{
    public int Key { get; set; }
    /// <summary>0=none, 1=Alt, 2=Ctrl, 4=Shift, 8=Win; combine with |</summary>
    public int Modifiers { get; set; }
}
