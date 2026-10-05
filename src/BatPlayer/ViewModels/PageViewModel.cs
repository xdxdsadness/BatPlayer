using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatPlayer.ViewModels;

/// <summary>
/// Базовый класс для всех вкладок главного окна.
/// </summary>
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private bool _isLoading;
}
