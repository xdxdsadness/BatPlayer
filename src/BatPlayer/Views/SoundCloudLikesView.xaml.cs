using System.Windows.Controls;
using System.Windows.Input;
using BatPlayer.ViewModels;

namespace BatPlayer.Views;

public partial class SoundCloudLikesView : UserControl
{
    public SoundCloudLikesView() => InitializeComponent();

    // Card click: local match -> file, otherwise resolve the stream.
    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListViewItem item) return;
        if (item.DataContext is not SoundCloudCard card) return;
        if (DataContext is not SoundCloudLikesViewModel vm) return;

        vm.PlayCardCommand.Execute(card);
        e.Handled = true;
    }

    // "+ add to playlist" on an SC card: build a runtime track from the card.
    private void AddToPlaylist_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: SoundCloudCard card })
        {
            var track = Helpers.SoundCloudRuntimeTracks.BuildRuntimeTrack(
                card.ScId, card.Title, card.Artist, card.DurationMs, card.ArtworkLocalPath, 0);
            PlaylistDialogs.AddTrackToPlaylist(track);
        }
    }
}
