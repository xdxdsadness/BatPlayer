using System;
using System.Drawing;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using BatPlayer.Audio;
using BatPlayer.Helpers;
using BatPlayer.Localization;

namespace BatPlayer.Services;

/// <summary>
/// System tray icon + context menu + track-change notifications.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly AudioService _audio;
    private TaskbarIcon? _icon;

    public TrayService(AudioService audio) => _audio = audio;

    public void Initialize()
    {
        // Re-initialization guard: the window is recreated on visual settings changes,
        // while TrayService is an app-level singleton. Without the guard, every window
        // recreation added a second tray icon (the old one leaked) and doubled the
        // subscriptions: track notifications arrived twice and more often.
        if (_icon != null) return;

        var appIcon = TryLoadAppIcon();
        _icon = new TaskbarIcon
        {
            Icon = appIcon,
            ToolTipText = Loc.Get("AppName"),
            Visibility = Visibility.Visible
        };

        _icon.ContextMenu = BuildMenu();
        // Rebuild the menu on language change (LanguageChanged arrives on the UI thread).
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

    // Items use an explicit implicit-style MenuItem with a type key — the tray menu
    // lives outside the window's logical tree, where implicit styles do not reach
    // (hover/padding of the default template differ from the track's context menu).
    private static System.Windows.Controls.MenuItem MakeItem(string header)
        => new()
        {
            Header = header,
            Style = (System.Windows.Style)System.Windows.Application.Current
                .FindResource(typeof(System.Windows.Controls.MenuItem))
        };

    // Separator — with an EXPLICIT keyed MenuSeparatorStyle: the tray menu lives
    // outside the window's logical tree (a context menu from the tray), the app-level
    // implicit Separator style never reaches it, and without the explicit assignment
    // the default "thick" WPF template with edge padding is drawn.
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

    /// <summary>Full application shutdown: remove the icon, stop audio, flush uncommitted
    /// listens and state, close the process. async void — a menu event handler;
    /// exceptions are swallowed (exit must not fail). await SaveStateAsync comes
    /// BEFORE Shutdown: after it the dispatcher stops and nothing can finish writing
    /// the listen/state.</summary>
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
