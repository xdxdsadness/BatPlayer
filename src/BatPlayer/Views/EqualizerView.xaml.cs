using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BatPlayer.Views;

public partial class EqualizerView : UserControl
{
    public EqualizerView() => InitializeComponent();

    /// <summary>Double-click on the EQ curve: add a band at the click point.</summary>
    private void Curve_AddNodeRequested(object? sender, (double Freq, double Gain) e)
        => (DataContext as ViewModels.EqualizerViewModel)?.AddBandCommand.Execute(e);

    /// <summary>Double-click on a curve node: remove the band.</summary>
    private void Curve_RemoveNodeRequested(object? sender, int index)
        => (DataContext as ViewModels.EqualizerViewModel)?.RemoveBandCommand.Execute(index);

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var name = PresetNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (DataContext is ViewModels.EqualizerViewModel vm)
        {
            vm.SaveAsPresetCommand.Execute(name);
            PresetNameBox.Text = string.Empty;
        }
    }
}
