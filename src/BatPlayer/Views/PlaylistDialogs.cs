using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BatPlayer.Localization;
using BatPlayer.Models;
using BatPlayer.Services;

namespace BatPlayer.Views;

/// <summary>
/// Диалоги плейлистов: выбор плейлиста для добавления трека (с созданием нового
/// по ходу) и ввод текста (переименование). Кодом, без XAML — маленькие служебные
/// окна в стилистике приложения: тёмная скруглённая панель, кнопки темы.
/// </summary>
public static class PlaylistDialogs
{
    private static PlaylistService PlaylistService =>
        (PlaylistService)App.Services.GetService(typeof(PlaylistService))!;
    private static LibraryService LibraryService =>
        (LibraryService)App.Services.GetService(typeof(LibraryService))!;

    /// <summary>
    /// Добавить трек в плейлист: окно со списком плейлистов + полем нового.
    /// Заполнили имя — создастся новый плейлист; иначе трек уйдёт в выбранный.
    /// Enter в поле и двойной клик по плейлисту подтверждают выбор.
    /// </summary>
    public static void AddTrackToPlaylist(Track track)
    {
        var playlists = LibraryService.GetAllPlaylistsAsync().GetAwaiter().GetResult();

        string? result = null;
        var selected = -1L;

        var stack = new StackPanel { Margin = new Thickness(18) };
        var win = MakeWindow(Loc.Get("AddToPlaylist"), stack);

        var newListBoxText = MakeLabel(Loc.Get("PlaylistNewName"));
        var nameBox = MakeTextBox();
        stack.Children.Add(newListBoxText);
        stack.Children.Add(nameBox);

        var listLabel = MakeLabel(Loc.Get("Playlists"));
        var list = MakePlaylistList();
        foreach (var p in playlists)
        {
            list.Items.Add(new ListBoxItem { Content = p.Name, Tag = p.Id });
        }
        // Список по размеру содержимого: один-два плейлиста не должны растягивать
        // диалог пустым полем; много — ограничение с прокруткой.
        list.Height = Math.Min(240, Math.Max(72, playlists.Count * 36 + 12));
        stack.Children.Add(listLabel);
        stack.Children.Add(list);

        void Confirm()
        {
            if (!string.IsNullOrWhiteSpace(nameBox.Text))
            {
                result = nameBox.Text.Trim();
                win.Close(); // новый плейлист
                return;
            }
            if (list.SelectedItem is ListBoxItem { Tag: long id })
            {
                selected = id;
                result = string.Empty; // выбранный существующий
                win.Close();
            }
        }

        var ok = MakeButton(Loc.Get("AddToPlaylist"), isAccent: true);
        ok.Click += (_, _) => Confirm();
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Confirm(); }
            else if (e.Key == Key.Escape) { e.Handled = true; win.Close(); }
        };
        list.MouseDoubleClick += (_, _) => Confirm();
        stack.Children.Add(ok);

        win.ShowDialog();
        ClearStuckHover();

        if (result == null) return; // отмена

        long playlistId;
        if (result.Length > 0)
            playlistId = PlaylistService.CreatePlaylistAsync(result).GetAwaiter().GetResult();
        else
            playlistId = selected;

        if (playlistId > 0)
            LibraryService.AddTrackToPlaylistAsync(playlistId, track).GetAwaiter().GetResult();
    }

    /// <summary>Ввод текста (переименование плейлиста). null — отмена.</summary>
    public static string? AskText(string title, string initial)
    {
        var stack = new StackPanel { Margin = new Thickness(18) };
        var win = MakeWindow(title, stack);

        var box = MakeTextBox();
        box.Text = initial;
        stack.Children.Add(box);

        string? result = null;
        var ok = MakeButton(Loc.Get("Save"), isAccent: true);
        ok.Click += (_, _) =>
        {
            result = box.Text.Trim();
            win.Close();
        };
        stack.Children.Add(ok);

        win.ShowDialog();
        ClearStuckHover();
        return result;
    }

    // ========================= UI-хелперы =========================

    private static Brush ThemeBrush(string key)
        => (Brush)Application.Current.FindResource(key);

    /// <summary>
    /// Окно в стилистике приложения: без системной рамки, тёмная скруглённая
    /// панель с заголовком и крестиком (как у окон логинов/главного окна).
    /// </summary>
    private static Window MakeWindow(string title, UIElement contentElement)
    {
        var headerText = MakeTitleText(title);
        var closeButton = MakeCloseButton();

        var header = new Grid { Margin = new Thickness(6, 2, 6, 2) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerText.SetValue(Grid.ColumnProperty, 0);
        closeButton.SetValue(Grid.ColumnProperty, 1);
        header.Children.Add(headerText);
        header.Children.Add(closeButton);

        var root = new StackPanel();
        root.Children.Add(header);

        // Панель вызывающего кода вставляется КАК ЕСТЬ (по ссылке): поля и кнопки
        // добавляются ПОСЛЕ MakeWindow, поэтому копирование детей в новую панель
        // при построении оставляло диалог пустым — только заголовок и крестик.
        if (contentElement is System.Windows.Controls.Panel panel)
            panel.Margin = new Thickness(18, 8, 18, 18);
        root.Children.Add(contentElement);

        var border = new Border
        {
            Background = ThemeBrush("PanelElevatedBrush"),
            BorderBrush = ThemeBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = root
        };

        var win = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = System.Windows.SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            // Фон окна прозрачный: скругление даёт Border внутри, непрозрачный фон
            // вылезал тёмными квадратами в углах за скруглённой рамкой.
            Background = Brushes.Transparent,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ResizeMode = ResizeMode.NoResize,
            Content = border
        };

        // Перетаскивание за заголовок.
        headerText.MouseLeftButtonDown += (_, _) => win.DragMove();
        closeButton.Click += (_, _) => win.Close();
        win.Loaded += (_, _) =>
        {
            if (contentElement is System.Windows.Controls.Panel panel) FocusFirstTextBox(panel);
        };
        return win;
    }

    private static TextBlock MakeTitleText(string text)
        => new()
        {
            Text = text,
            Margin = new Thickness(12, 10, 0, 8),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("TextPrimaryBrush")
        };

    /// <summary>Крестик в стилистике главного окна: тот же IconButton (круглый,
    /// со scale-анимацией hover/press). Кнопка без стиля показывала системный
    /// голубой ховер WPF, не сочетавшийся с тёмной темой.</summary>
    private static Button MakeCloseButton()
    {
        var path = new System.Windows.Shapes.Path
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Data = (Geometry)Application.Current.FindResource("IconClose")
        };
        // Fill следует за Foreground кнопки: IconButton на hover меняет именно его.
        path.SetBinding(System.Windows.Shapes.Path.FillProperty,
            new System.Windows.Data.Binding("Foreground")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(
                    System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1)
            });
        return new Button
        {
            Content = path,
            Margin = new Thickness(0, 6, 6, 0),
            Style = (Style)Application.Current.FindResource("IconButton"),
            Width = 28,
            Height = 28,
            Cursor = Cursors.Hand
        };
    }

    private static void FocusFirstTextBox(System.Windows.Controls.Panel panel)
    {
        foreach (System.Windows.UIElement child in panel.Children)
            if (child is TextBox box) { box.Focus(); box.SelectAll(); return; }
    }

    /// <summary>Джиттл курсора на 1px после модального диалога: пока диалог открыт,
    /// окно disabled и WPF не обрабатывает уход мыши — IsMouseOver карточек залипает.
    /// Реальный WM_MOUSEMOVE заставляет WPF пересчитать hover-состояния.</summary>
    private static void ClearStuckHover()
    {
        if (GetCursorPos(out var p))
        {
            SetCursorPos(p.X + 1, p.Y);
            SetCursorPos(p.X, p.Y);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private static TextBlock MakeLabel(string text)
        => new()
        {
            Text = text,
            Margin = new Thickness(0, 10, 0, 4),
            FontSize = 12,
            Foreground = ThemeBrush("TextSecondaryBrush")
        };

    /// <summary>Поле ввода в стилистике приложения: стиль DarkTextBox даёт скруглённые
    /// углы и тематическую рамку фокуса (без стиля WPF рисовал системный синий бордер
    /// и прямые углы). Локально задаём только то, что отличается от стиля.</summary>
    private static TextBox MakeTextBox()
        => new()
        {
            Height = 34,
            Style = (Style)Application.Current.FindResource("DarkTextBox"),
            FontSize = 13
        };

    /// <summary>Список плейлистов: тёмная панель, скруглённая, пункты без системного chrome.</summary>
    private static ListBox MakePlaylistList()
    {
        var list = new ListBox
        {
            Height = 200,
            Background = ThemeBrush("SecondaryBrush"),
            Foreground = ThemeBrush("TextPrimaryBrush"),
            BorderThickness = new Thickness(0),
            ItemContainerStyle = MakePlaylistItemStyle()
        };
        System.Windows.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        return list;
    }

    private static Style MakePlaylistItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        // Системный chrome пунктов убираем своим шаблоном, hover — мягкая подсветка.
        style.Setters.Add(new Setter(Control.TemplateProperty, MakeItemTemplate()));
        return style;
    }

    private static ControlTemplate MakeItemTemplate()
    {
        var template = new ControlTemplate(typeof(ListBoxItem));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bd";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        // Внутренние поля пункта: имя не прилипает к краю подсветки hover'а
        presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 8, 12, 8));
        border.AppendChild(presenter);
        template.VisualTree = border;

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, ThemeBrush("HoverBrush"), "Bd"));
        template.Triggers.Add(hoverTrigger);

        var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, ThemeBrush("SelectedBrush"), "Bd"));
        template.Triggers.Add(selectedTrigger);

        return template;
    }

    private static Button MakeButton(string text, bool isAccent)
        => new()
        {
            Content = new TextBlock { Text = text, FontSize = 12 },
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.FindResource(isAccent ? "AccentButton" : "PrimaryButton"),
            Cursor = Cursors.Hand
        };
}
