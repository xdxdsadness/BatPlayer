using System.Windows.Controls;
using System.Windows.Input;
using BatPlayer.ViewModels;

namespace BatPlayer.Views;

public partial class YmMusicView : UserControl
{
    public YmMusicView() => InitializeComponent();

    // Card click: local match -> file, otherwise resolve the stream.
    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListViewItem item) return;
        if (item.DataContext is not YmCard card) return;
        if (DataContext is not YmMusicViewModel vm) return;

        vm.PlayCardCommand.Execute(card);
        e.Handled = true;
    }

    // "+ add to playlist" on a YM card: build a runtime track from the card.
    private void AddToPlaylist_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: YmCard card })
        {
            var track = Helpers.YmRuntimeTracks.BuildRuntimeTrack(
                card.YmId, card.Title, card.Artist, card.DurationMs, card.ArtworkLocalPath, card.Streamable, 0);
            PlaylistDialogs.AddTrackToPlaylist(track);
        }
    }
}
