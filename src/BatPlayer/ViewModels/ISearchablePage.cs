namespace BatPlayer.ViewModels;

/// <summary>
/// Страница, контент которой фильтруется универсальной поисковой строкой в шапке окна.
/// Реализуют платформенные сетки (SoundCloud/Spotify/VK/Яндекс Музыка), «Волна» и «Загрузки»;
/// страницы библиотеки (Home/Недавние/Любимые) работают через Library.SearchText —
/// их поиск ищет ещё и по платформенным метаданным (см. LibraryViewModel.ApplySearchAsync).
/// </summary>
public interface ISearchablePage
{
    /// <summary>Применить поисковый запрос к контенту страницы; пустая строка — полный список.</summary>
    void ApplySearch(string? query);
}
