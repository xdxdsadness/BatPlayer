using System.Threading.Tasks;
using System.Windows;
using BatPlayer.Views;

namespace BatPlayer.Services.Vk;

/// <summary>
/// Открывает окно входа VK (WebView2) как модальный диалог. После успешного входа
/// cookies веб-сессии уже сохранены окном через VkService.SaveSessionCookies — сервис
/// возвращает true, вызывающий (настройки) обновляет статус. Пользователь закрыл окно
/// не залогинившись — false.
/// </summary>
public sealed class VkLoginService
{
    private readonly VkService _vk;

    public VkLoginService(VkService vk) => _vk = vk;

    /// <summary>true — вход выполнен, cookies веб-сессии сохранены в vk_auth.json.</summary>
    public async Task<bool> LoginAsync(Window owner)
    {
        var window = new VkLoginWindow(_vk) { Owner = owner };
        // ShowDialog блокирует UI-поток только своим окном — приложение продолжает жить.
        var result = window.ShowDialog() == true;
        await Task.CompletedTask; // async-сигнатура на будущее (ожидание подтверждения сессии)
        return result;
    }
}
