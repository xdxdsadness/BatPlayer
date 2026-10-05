using System.Windows.Controls;
using System.Windows.Input;
using BatPlayer.ViewModels;

namespace BatPlayer.Views;

public partial class ArtistsView : UserControl
{
    public ArtistsView() => InitializeComponent();

    // Клик по карточке открывает профиль исполнителя.
    private void ArtistCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListViewItem item) return;
        if (item.DataContext is not ArtistCard card) return;
        if (DataContext is not ArtistsViewModel vm) return;

        vm.OpenArtistCommand.Execute(card);
        e.Handled = true;
    }
}
