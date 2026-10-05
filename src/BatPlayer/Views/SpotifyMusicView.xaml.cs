using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BatPlayer.ViewModels;

namespace BatPlayer.Views;

public partial class SpotifyMusicView : UserControl
{
    private SpotifyMusicViewModel? ViewModel => DataContext as SpotifyMusicViewModel;

    public SpotifyMusicView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
            await ViewModel.OnNavigatedAsync();
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem item && item.Content is SpotifyCard card)
            ViewModel?.PlayCardCommand.Execute(card);
    }
}
