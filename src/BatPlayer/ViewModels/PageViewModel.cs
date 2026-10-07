using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatPlayer.ViewModels;

/// <summary>
/// Base class for all main-window tabs.
/// </summary>
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private bool _isLoading;
}
