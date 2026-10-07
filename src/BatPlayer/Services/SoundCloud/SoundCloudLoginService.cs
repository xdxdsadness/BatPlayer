using System.Threading.Tasks;
using System.Windows;
using BatPlayer.Views;

namespace BatPlayer.Services.SoundCloud;

/// <summary>
/// Opens the SoundCloud login window (WebView2) as a modal dialog. After a successful
/// login the cookies have already been saved by the window via SaveSessionCookies —
/// the service returns true and the caller (Settings) refreshes the status.
/// </summary>
public sealed class SoundCloudLoginService
{
    private readonly SoundCloudService _soundCloud;

    public SoundCloudLoginService(SoundCloudService soundCloud) => _soundCloud = soundCloud;

    /// <summary>true — logged in, cookies saved to sc_auth.json.</summary>
    /// <param name="signOutFirst">Open the site's own logout first ("switch account" mode):
    /// the user signs out and back in with another account in the original SoundCloud web
    /// UI, and the app picks up the new session.</param>
    public async Task<bool> LoginAsync(Window owner, bool signOutFirst = false)
    {
        var window = new SoundCloudLoginWindow(_soundCloud, signOutFirst) { Owner = owner };
        // ShowDialog blocks only its own window — the app keeps running.
        var result = window.ShowDialog() == true;
        await Task.CompletedTask; // async signature for the future (session confirmation wait)
        return result;
    }
}
