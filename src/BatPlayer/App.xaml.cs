using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Shell;
using BatPlayer.Audio;
using BatPlayer.Database;
using BatPlayer.Localization;
using BatPlayer.Services;
using BatPlayer.Views;

namespace BatPlayer;

public partial class App : Application
{
    // Explicit taskbar identity: without it the shell uses a name cached by exe
    // path — after a rebuild the taskbar menu kept the old "BatPlayer".
    private const string AppUserModelId = "BatPlayer";

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appID);
    public static string AppDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatPlayer");

    public static string DatabasePath { get; } = Path.Combine(AppDataDir, "batplayer.db");
    public static string SettingsPath { get; } = Path.Combine(AppDataDir, "settings.json");
    public static string CoverCacheDir { get; } = Path.Combine(AppDataDir, "cover_cache");

    private static IServiceProvider? _services;
    public static IServiceProvider Services => _services ?? throw new InvalidOperationException("Services not initialized");

    /// <summary>True while the app is deliberately exiting (tray "Exit") — window close must not hide to tray.</summary>
    public static bool IsExiting { get; set; }

    private const string MutexName = @"Local\BatPlayer.Instance";
    private const string ExitEventName = @"Local\BatPlayer.Exit";
    private const string ActivateEventName = @"Local\BatPlayer.Activate";

    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _exitSignal;
    private static EventWaitHandle? _activateSignal;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One-time data migration after the ObsidianPlayer → BatPlayer rename:
        // moves the old LocalApplicationData folder (db, settings, auth).
        try
        {
            var oldAppData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ObsidianPlayer");
            if (!Directory.Exists(AppDataDir) && Directory.Exists(oldAppData))
                Directory.Move(oldAppData, AppDataDir);
        }
        catch (Exception ex) { Logger.Warn($"AppData migration failed: {ex.Message}"); }

        // One-time database file rename left from the same rename (obsidian.db -> batplayer.db).
        try
        {
            var legacyDb = Path.Combine(AppDataDir, "obsidian.db");
            if (!File.Exists(DatabasePath) && File.Exists(legacyDb))
            {
                File.Move(legacyDb, DatabasePath);
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var side = legacyDb + suffix;
                    if (File.Exists(side)) File.Move(side, DatabasePath + suffix);
                }
            }
        }
        catch (Exception ex) { Logger.Warn($"Database file rename failed: {ex.Message}"); }

        // Taskbar identity must be set BEFORE the jump list and any windows.
        try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
        catch (Exception ex) { Logger.Warn($"SetCurrentProcessExplicitAppUserModelID failed: {ex.Message}"); }

        // Register the code pages provider (incl. Windows-1251); required for
        // self-contained apps on .NET 8+.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        // Single instance: a second launch either tells the running one to exit
        // (taskbar jump list "Exit") or activates it, then dies.
        _instanceMutex = new Mutex(true, MutexName, out var isNew);
        Logger.Info($"Startup: args=[{string.Join(" ", e.Args)}] isNew={isNew}");
        if (!isNew)
        {
            if (e.Args.Any(a => string.Equals(a, "--exit", StringComparison.OrdinalIgnoreCase)))
            {
                if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exit)) { exit.Set(); exit.Dispose(); }
            }
            else if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var activate))
            {
                activate.Set();
                activate.Dispose();
            }
            Shutdown(0);
            return;
        }

        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        ThreadPool.RegisterWaitForSingleObject(_exitSignal,
            (_, _) => Dispatcher.BeginInvoke(() =>
                (_services?.GetService(typeof(TrayService)) as TrayService)?.ExitApplication()),
            null, -1, executeOnlyOnce: false);
        ThreadPool.RegisterWaitForSingleObject(_activateSignal,
            (_, _) => Dispatcher.BeginInvoke(() =>
                (_services?.GetService(typeof(TrayService)) as TrayService)?.ShowMainWindow()),
            null, -1, executeOnlyOnce: false);

        // Apply UI language before anything user-visible is created (jump list included).
        // SettingsService falls back to defaults (Language="en") if the file is missing or broken.
        Loc.SetLanguage(new SettingsService(SettingsPath).Current.Language);

        // Taskbar right-click menu (jump list)
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            var jumpList = new JumpList();
            jumpList.JumpItems.Add(new JumpTask
            {
                Title = Loc.Get("ShowWindow"),
                Description = Loc.Get("ShowWindowDesc"),
                ApplicationPath = exePath,
                Arguments = "--show"
            });
            jumpList.JumpItems.Add(new JumpTask
            {
                Title = Loc.Get("Exit"),
                Description = Loc.Get("ExitDesc"),
                ApplicationPath = exePath,
                Arguments = "--exit"
            });
            JumpList.SetJumpList(this, jumpList);
            jumpList.Apply();
        }

        try
        {
            Directory.CreateDirectory(AppDataDir);
            Directory.CreateDirectory(CoverCacheDir);

            await DatabaseContext.InitializeAsync(DatabasePath);
            _services = ServiceContainer.Build(DatabasePath, SettingsPath, CoverCacheDir);

            DispatcherUnhandledException += (_, args) =>
            {
                Logger.Error(args.Exception, "Unhandled UI exception");
                args.Handled = true;
            };

            Logger.Info("Bat Player started.");

            // Theme/accent before the window: StaticResource brushes resolve while
            // MainWindow loads and pick up the overridden resources.
            if (_services?.GetService(typeof(SettingsService)) is SettingsService ss)
                ApplyVisualResources(ss.Current);

            var window = new MainWindow();
            window.Show();

            // Built-in DPI bypass: probe access in the background; on blocking, start
            // the packet-level engine (UAC once per session) so the first click plays.
            if (_services?.GetService(typeof(SettingsService)) is SettingsService bypassSettings)
                BatPlayer.Services.DpiBypass.StartWatchdog(bypassSettings.Current.DpiBypassEnabled);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Startup failed");
            MessageBox.Show($"{Loc.Get("ErrorStartup")}:\n{ex.Message}", Loc.Get("AppName"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Applies visual settings to app resources: theme, accent, page transparency
    /// (for the GIF background), cover shadows. Called at startup and by the
    /// "Apply" button — a recreated window picks up the StaticResource values.
    /// </summary>
    public static void ApplyVisualResources(Models.AppSettings settings)
    {
        ThemeService.Apply(settings);

        // The GIF background shows only through transparent pages; when disabled,
        // pages revert to the theme background.
        Application.Current.Resources["PageBackgroundBrush"] =
            settings.BackgroundGifEnabled
                ? System.Windows.Media.Brushes.Transparent
                : Application.Current.Resources["BgBrush"];

        // Cover shadows: null out the resource effect when disabled.
        Application.Current.Resources["CardCoverShadow"] =
            settings.CoverShadows
                ? new System.Windows.Media.Effects.DropShadowEffect
                  {
                      BlurRadius = 12, ShadowDepth = 3, Direction = 270,
                      Opacity = 0.32, Color = System.Windows.Media.Colors.Black,
                      RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
                  }
                : null;
    }

    /// <summary>
    /// Applies visual settings immediately, without restarting the app: the window
    /// is recreated with the new resources (StaticResource resolves at load time);
    /// the audio service lives at app level, so playback is not interrupted.
    /// </summary>
    public static void ApplyVisualSettingsAndRecreateWindow()
    {
        if (_services?.GetService(typeof(SettingsService)) is SettingsService ss)
            ApplyVisualResources(ss.Current);

        var old = Application.Current.MainWindow;
        // Hide the old window BEFORE building the new one: otherwise the user
        // stares at a frozen old frame while the window is rebuilt, and windows
        // repainting over each other cause extra full redraws.
        old?.Hide();
        var fresh = new MainWindow(restorePlayback: false);
        Application.Current.MainWindow = fresh;
        fresh.Show();
        (old as MainWindow)?.DestroyForRecreate();
        Logger.Info("Main window recreated to apply visual settings.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_services?.GetService(typeof(TrayService)) is TrayService tray) tray.Dispose();
            (_services?.GetService(typeof(AudioService)) as AudioService)?.Dispose();
            Logger.Info("Bat Player closed.");
        }
        catch { }
        base.OnExit(e);
    }
}
