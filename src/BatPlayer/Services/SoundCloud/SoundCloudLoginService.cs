using System.Threading.Tasks;
using System.Windows;
using BatPlayer.Views;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Открывает окно входа SoundCloud (WebView2) как модальный диалог.
/// После успешного входа cookies уже сохранены окном через SaveSessionCookies —
/// сервис возвращает true, вызывающий (настройки) обновляет статус.
/// </summary>
public sealed class SoundCloudLoginService
{
    private readonly SoundCloudService _soundCloud;

    public SoundCloudLoginService(SoundCloudService soundCloud) => _soundCloud = soundCloud;

    /// <summary>true — вход выполнен, cookies сохранены в sc_auth.json.</summary>
    /// <param name="signOutFirst">Сначала открыть штатный logout на сайте — режим
    /// «Сменить аккаунт»: пользователь выходит и входит другим аккаунтом в оригинальном
    /// веб-интерфейсе SoundCloud, приложение подхватывает новую сессию.</param>
    public async Task<bool> LoginAsync(Window owner, bool signOutFirst = false)
    {
        var window = new SoundCloudLoginWindow(_soundCloud, signOutFirst) { Owner = owner };
        // ShowDialog блокирует UI-поток только своим окном — приложение продолжает жить.
        var result = window.ShowDialog() == true;
        await Task.CompletedTask; // async-сигнатура на будущее (ожидание подтверждения сессии)
        return result;
    }
}
