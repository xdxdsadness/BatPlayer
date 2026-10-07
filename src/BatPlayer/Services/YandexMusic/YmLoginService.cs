using System.Threading.Tasks;
using System.Windows;
using BatPlayer.Views;

namespace BatPlayer.Services.YandexMusic;

/// <summary>
/// Opens the Yandex Music login window (WebView2, Yandex ID OAuth) as a modal dialog.
/// After a successful sign-in the OAuth token is already saved by the window via
/// YmService.SaveSessionOAuth — the service returns true and the caller (settings)
/// updates the status. If the user closes the window without signing in — false.
/// </summary>
public sealed class YmLoginService
{
    private readonly YmService _ym;

    public YmLoginService(YmService ym) => _ym = ym;

    /// <summary>true — signed in, OAuth token saved to ym_auth.json.</summary>
    public async Task<bool> LoginAsync(Window owner)
    {
        var window = new YmLoginWindow(_ym) { Owner = owner };
        // ShowDialog blocks the UI thread only for its own window — the app keeps running.
        var result = window.ShowDialog() == true;
        await Task.CompletedTask; // async signature reserved (future session confirmation)
        return result;
    }
}
