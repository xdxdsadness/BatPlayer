using System.Windows.Controls;
using System.Windows.Input;
using BatPlayer.ViewModels;

namespace BatPlayer.Views;

public partial class VkMusicView : UserControl
{
    public VkMusicView() => InitializeComponent();

    // Card click: local match -> file, otherwise resolve the stream.
    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListViewItem item) return;
        if (item.DataContext is not VkCard card) return;
        if (DataContext is not VkMusicViewModel vm) return;

        vm.PlayCardCommand.Execute(card);
        e.Handled = true;
    }

    // "+ add to playlist" on a VK card: build a runtime track from the card.
    private void AddToPlaylist_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: VkCard card })
        {
            var track = Helpers.VkRuntimeTracks.BuildRuntimeTrack(
                card.VkId, card.Title, card.Artist, card.DurationMs, card.ArtworkLocalPath, 0);
            PlaylistDialogs.AddTrackToPlaylist(track);
        }
    }
}
