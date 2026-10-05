using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BatPlayer.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>Кнопка «Применить»: визуальные настройки вступают в силу сразу —
    /// главное окно пересоздаётся с новыми ресурсами, музыка не прерывается.</summary>
    private void ApplySettings_Click(object sender, System.Windows.RoutedEventArgs e)
        => App.ApplyVisualSettingsAndRecreateWindow();

    /// <summary>
    /// Колесо при открытом выпадающем списке (аудиоустройства, язык, колонки):
    /// ComboBox держит захват мыши, событие булькает через страницу и прокручивает
    /// её под открытым Popup — меню «уезжает» от своего комбобокса (Popup не следует
    /// за перемещённой целью). Пока список держит захват, колесо над страницей гасим.
    /// Колесо над самим открытым меню в этот Preview не попадает — оно
    /// роутится внутри Popup и продолжает прокручивать список устройств.
    /// </summary>
    private void SettingsScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Mouse.Captured is ComboBox or ComboBoxItem)
            e.Handled = true;
    }

    /// <summary>
    /// Страховка к гашению колеса: часть сценариев всё же прокручивает страницу
    /// при открытом дропдауне (захват ушёл от комбобокса, скроллбар попапа и т.п.),
    /// и Popup не следует за целью — меню оставалось висеть на старом месте.
    /// Любая фактическая прокрутка закрывает все открытые дропдауны страницы.
    /// </summary>
    private void SettingsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.HorizontalChange == 0) return;
        CloseOpenComboBoxPopups(this);
    }

    private static void CloseOpenComboBoxPopups(DependencyObject node)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is ComboBox { IsDropDownOpen: true } combo)
                combo.IsDropDownOpen = false;
            CloseOpenComboBoxPopups(child);
        }
    }
}
