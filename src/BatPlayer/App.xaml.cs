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
    // Явная идентичность на панели задач: без неё шелл выводит имя из кэша по пути
    // exe — при замене сборки в меню панели задач оставалось старое «BatPlayer».
    private const string AppUserModelId = "BatPlayer";

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appID);
    public static string AppDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatPlayer");

    public static string DatabasePath { get; } = Path.Combine(AppDataDir, "obsidian.db");
    public static string SettingsPath { get; } = Path.Combine(AppDataDir, "settings.json");
    public static string CoverCacheDir { get; } = Path.Combine(AppDataDir, "cover_cache");

    private static IServiceProvider? _services;
    public static IServiceProvider Services => _services ?? throw new InvalidOperationException("Services not initialized");

    /// <summary>True while the app is deliberately exiting (tray "Выход") — window close must not hide to tray.</summary>
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

        // Переезд данных после переименования проекта ObsidianPlayer → BatPlayer:
        // один раз переносим старую папку LocalApplicationData (база, настройки, авторизации).
        try
        {
            var oldAppData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ObsidianPlayer");
            if (!Directory.Exists(AppDataDir) && Directory.Exists(oldAppData))
                Directory.Move(oldAppData, AppDataDir);
        }
        catch (Exception ex) { Logger.Warn($"AppData migration failed: {ex.Message}"); }

        // Идентичность панели задач — ДО jump list и любых окон.
        try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
        catch (Exception ex) { Logger.Warn($"SetCurrentProcessExplicitAppUserModelID failed: {ex.Message}"); }

        // Регистрация Code Page провайдера для поддержки кодировок (включая Windows-1251)
        // Необходимо для self-contained приложений в .NET 8+
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        // Single instance: a second launch either tells the running one to exit
        // (taskbar jump list "Выход") or activates it, then dies.
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

            // Тема/акцент — до создания окна: StaticResource-кисти резолвятся при
            // загрузке MainWindow и подхватывают перезаписанные ресурсы.
            if (_services?.GetService(typeof(SettingsService)) is SettingsService ss)
                ApplyVisualResources(ss.Current);

            var window = new MainWindow();
            window.Show();

            // Антиблокировка SoundCloud: фоново проверяем прямой доступ и при блокировке
            // поднимаем zapret (UAC — один раз за сессию), чтобы первый клик уже играл.
            if (_services?.GetService(typeof(SettingsService)) is SettingsService startupSettings
                && _services.GetService(typeof(global::BatPlayer.Services.SoundCloud.SoundCloudService)) is global::BatPlayer.Services.SoundCloud.SoundCloudService)
            {
                global::BatPlayer.Services.SoundCloud.SoundCloudZapret.StartWatchdog(startupSettings.Current.SoundCloudZapretEnabled);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Startup failed");
            MessageBox.Show($"{Loc.Get("ErrorStartup")}:\n{ex.Message}", Loc.Get("AppName"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Применение визуальных настроек к ресурсам приложения: тема, акцент,
    /// прозрачность страниц (для GIF-фона), тени обложек. Вызывается на старте
    /// и по кнопке «Применить» — StaticResource при пересоздании окна подхватывает.
    /// </summary>
    public static void ApplyVisualResources(Models.AppSettings settings)
    {
        ThemeService.Apply(settings);

        // GIF-фон виден только сквозь прозрачные страницы; выключен — страницы
        // снова залиты фоном темы.
        Application.Current.Resources["PageBackgroundBrush"] =
            settings.BackgroundGifEnabled
                ? System.Windows.Media.Brushes.Transparent
                : Application.Current.Resources["BgBrush"];

        // Тени обложек: ресурс-эффект в null, когда выключены.
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
    /// Применить визуальные настройки СРАЗУ, без перезапуска приложения: окно
    /// пересоздаётся с новыми ресурсами (StaticResource резолвятся при загрузке),
    /// аудио-сервис живёт на уровне приложения — воспроизведение не прерывается.
    /// </summary>
    public static void ApplyVisualSettingsAndRecreateWindow()
    {
        if (_services?.GetService(typeof(SettingsService)) is SettingsService ss)
            ApplyVisualResources(ss.Current);

        var old = Application.Current.MainWindow;
        // Старое окно прячем ДО сборки нового: иначе на время пересоздания (тяжёлая
        // разметка + загрузка страниц) пользователь смотрит на замерший старый кадр,
        // а взаимно перекрашивающиеся окна дают лишние полные перерисовки.
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
