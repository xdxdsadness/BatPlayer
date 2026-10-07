using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BatPlayer.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>Apply button: visual settings take effect immediately —
    /// the main window is recreated with new resources; playback is not interrupted.</summary>
    private void ApplySettings_Click(object sender, System.Windows.RoutedEventArgs e)
        => App.ApplyVisualSettingsAndRecreateWindow();

    /// <summary>
    /// While a combo dropdown is open, the wheel bubbles through the page and scrolls it
    /// under the open Popup, which doesn't follow its moved target. Suppress the wheel
    /// over the page while the list holds capture; the wheel over the open menu itself
    /// is routed inside the Popup and keeps scrolling the device list.
    /// </summary>
    private void SettingsScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Mouse.Captured is ComboBox or ComboBoxItem)
            e.Handled = true;
    }

    /// <summary>
    /// Safety net for the wheel suppression: some scenarios still scroll the page with
    /// a dropdown open, and the Popup doesn't follow the target. Any actual scroll
    /// closes all open dropdowns on the page.
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
