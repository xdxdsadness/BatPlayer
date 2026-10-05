using System.Threading.Tasks;
using System.Windows;
using BatPlayer.Views;

namespace BatPlayer.Services.YandexMusic;

/// <summary>
/// Открывает окно входа Яндекс Музыки (WebView2, OAuth Яндекс ID) как модальный диалог.
/// После успешного входа OAuth-токен уже сохранён окном через YmService.SaveSessionOAuth —
/// сервис возвращает true, вызывающий (настройки) обновляет статус. Пользователь закрыл
/// окно не залогинившись — false.
/// </summary>
public sealed class YmLoginService
{
    private readonly YmService _ym;

    public YmLoginService(YmService ym) => _ym = ym;

    /// <summary>true — вход выполнен, OAuth-токен сохранён в ym_auth.json.</summary>
    public async Task<bool> LoginAsync(Window owner)
    {
        var window = new YmLoginWindow(_ym) { Owner = owner };
        // ShowDialog блокирует UI-поток только своим окном — приложение продолжает жить.
        var result = window.ShowDialog() == true;
        await Task.CompletedTask; // async-сигнатура на будущее (ожидание подтверждения сессии)
        return result;
    }
}
