namespace BatPlayer.ViewModels;

/// <summary>
/// Page whose content is filtered by the universal search bar in the window header.
/// Implemented by platform grids (SoundCloud/Spotify/VK/Yandex Music), "Wave" and "Downloads";
/// library pages (Home/Recent/Favorites) work via Library.SearchText —
/// their search also covers platform metadata (see LibraryViewModel.ApplySearchAsync).
/// </summary>
public interface ISearchablePage
{
    /// <summary>Applies the search query to the page content; empty string means the full list.</summary>
    void ApplySearch(string? query);
}
