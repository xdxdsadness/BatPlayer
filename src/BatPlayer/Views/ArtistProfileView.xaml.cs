using System.Windows.Controls;
using System.Windows.Input;
using BatPlayer.Models;
using BatPlayer.ViewModels;

namespace BatPlayer.Views;

public partial class ArtistProfileView : UserControl
{
    public ArtistProfileView() => InitializeComponent();

    private void TrackItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListViewItem item) return;
        if (item.DataContext is not Track track) return;
        if (DataContext is not ArtistProfileViewModel vm) return;

        vm.PlayTrackCommand.Execute(track);
        e.Handled = true;
    }

    // «+ в плейлист»: трек карточки (локальный или runtime-карточка платформы).
    private void AddToPlaylist_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: Models.Track track })
            PlaylistDialogs.AddTrackToPlaylist(track);
    }
}
