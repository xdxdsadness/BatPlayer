using System.Windows.Controls;
using System.Windows.Input;
using BatPlayer.ViewModels;

namespace BatPlayer.Views;

public partial class YmMusicView : UserControl
{
    public YmMusicView() => InitializeComponent();

    // Клик по карточке: локальный матч -> файл, иначе разрешение стрима.
    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListViewItem item) return;
        if (item.DataContext is not YmCard card) return;
        if (DataContext is not YmMusicViewModel vm) return;

        vm.PlayCardCommand.Execute(card);
        e.Handled = true;
    }

    // Кнопка «+ в плейлист» на YM-карточке: строим runtime-трек по карточке.
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
