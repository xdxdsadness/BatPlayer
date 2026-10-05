using System.Windows.Controls;
using System.Windows.Input;
using BatPlayer.ViewModels;

namespace BatPlayer.Views;

public partial class VkMusicView : UserControl
{
    public VkMusicView() => InitializeComponent();

    // Клик по карточке: локальный матч -> файл, иначе разрешение стрима.
    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListViewItem item) return;
        if (item.DataContext is not VkCard card) return;
        if (DataContext is not VkMusicViewModel vm) return;

        vm.PlayCardCommand.Execute(card);
        e.Handled = true;
    }

    // Кнопка «+ в плейлист» на VK-карточке: строим runtime-трек по карточке.
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
