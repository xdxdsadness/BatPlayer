using System;
using System.Drawing;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using BatPlayer.Audio;
using BatPlayer.Helpers;
using BatPlayer.Localization;

namespace BatPlayer.Services;

/// <summary>
/// Иконка в системном трее + контекстное меню + уведомления о смене трека.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly AudioService _audio;
    private TaskbarIcon? _icon;

    public TrayService(AudioService audio) => _audio = audio;

    public void Initialize()
    {
        // Guard от повторной инициализации: окно пересоздаётся при смене визуальных
        // настроек, а TrayService — синглтон уровня приложения. Без guard'а каждое
        // пересоздание окна вешало в трей вторую иконку (старая утекала) и удваивало
        // подписки: уведомления о треке приходили дважды и чаще.
        if (_icon != null) return;

        var appIcon = TryLoadAppIcon();
        _icon = new TaskbarIcon
        {
            Icon = appIcon,
            ToolTipText = Loc.Get("AppName"),
            Visibility = Visibility.Visible
        };

        _icon.ContextMenu = BuildMenu();
        // Перестраиваем меню на смене языка (LanguageChanged приходит с UI-потока).
        Loc.LanguageChanged += OnLanguageChanged;
        _audio.CurrentTrackChanged += OnTrackChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_icon != null) _icon.ContextMenu = BuildMenu();
    }

    private static System.Drawing.Icon TryLoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                return System.Drawing.Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application;
        }
        catch { /* fall back */ }
        return SystemIcons.Application;
    }

    private System.Windows.Controls.ContextMenu BuildMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        menu.Style = (System.Windows.Style)System.Windows.Application.Current.FindResource("TrayContextMenuStyle");

        var miPrev = MakeItem(Loc.Get("Previous"));
        miPrev.Click += (_, _) => _audio.Previous();
        var miPlayPause = MakeItem(Loc.Get("PlayPause"));
        miPlayPause.Click += (_, _) => _audio.PlayPauseToggle();
        var miNext = MakeItem(Loc.Get("Next"));
        miNext.Click += (_, _) => _audio.Next();

        var miShow = MakeItem(Loc.Get("ShowWindow"));
        miShow.Click += (_, _) => ShowMainWindow();

        var miExit = MakeItem(Loc.Get("Exit"));
        miExit.Click += (_, _) => ExitApplication();

        menu.Items.Add(miPrev);
        menu.Items.Add(miPlayPause);
        menu.Items.Add(miNext);
        menu.Items.Add(MakeSeparator());
        menu.Items.Add(miShow);
        menu.Items.Add(miExit);
        return menu;
    }

    // Пункты: явный implicit-стиль MenuItem по type-ключу — трей-меню живёт вне
    // логического дерева окна, implicit-стили туда не доходят (hover/отступы
    // дефолтного шаблона отличаются от ПКМ-меню трека).
    private static System.Windows.Controls.MenuItem MakeItem(string header)
        => new()
        {
            Header = header,
            Style = (System.Windows.Style)System.Windows.Application.Current
                .FindResource(typeof(System.Windows.Controls.MenuItem))
        };

    // Сепаратор — с ЯВНЫМ keyed-стилем MenuSeparatorStyle: трей-меню живёт вне
    // логического дерева окна (ContextMenu из трея), app-уровневый implicit-стиль
    // Separator туда не доходит, и без явного назначения рисуется дефолтный
    // «жирный» шаблон WPF с отступами от краёв.
    private static System.Windows.Controls.Separator MakeSeparator()
        => new()
        {
            Style = (System.Windows.Style)System.Windows.Application.Current
                .FindResource("MenuSeparatorStyle")
        };

    private void OnTrackChanged(object? sender, Models.Track? t)
    {
        if (t == null) return;
        var title = $"▶ {t.DisplayTitle}";
        var text = $"{t.DisplayArtist}\n{t.DisplayAlbum}";
        try { _icon?.ShowBalloonTip(title, text, BalloonIcon.Info); } catch { /* ignore */ }
    }

    public void ShowMainWindow()
    {
        var w = System.Windows.Application.Current.MainWindow;
        if (w != null)
        {
            w.Show();
            w.WindowState = WindowState.Normal;
            w.Activate();
        }
    }

    /// <summary>Полное завершение работы приложения: убрать иконку, остановить звук,
    /// дозаписать незакоммиченную прослушку и состояние, закрыть процесс.
    /// async void — обработчик события меню; исключения глотаются (exit must not fail),
    /// await SaveStateAsync стоит ДО Shutdown: после него dispatcher останавливается
    /// и продолжить запись прослушки/состояния некому.</summary>
    public async void ExitApplication()
    {
        Logger.Info("Exit requested.");
        App.IsExiting = true;
        try
        {
            _audio.Stop();
            await _audio.SaveStateAsync();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Save state on exit failed");
        }

        var app = System.Windows.Application.Current;
        if (app == null) return;
        app.Shutdown();

        // Shutdown is graceful; make sure the process never lingers as a ghost
        // in the tray.
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            Environment.Exit(0);
        });
    }

    public void Dispose()
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        _audio.CurrentTrackChanged -= OnTrackChanged;
        _icon?.Dispose();
        _icon = null;
    }
}
