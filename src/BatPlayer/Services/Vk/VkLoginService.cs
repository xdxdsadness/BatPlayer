using System.Threading.Tasks;
using System.Windows;
using BatPlayer.Views;

namespace BatPlayer.Services.Vk;

/// <summary>
/// Opens the VK login window (WebView2) as a modal dialog. After a successful sign-in
/// the web session cookies are already saved by the window via VkService.SaveSessionCookies —
/// the service returns true and the caller (settings) updates the status. If the user
/// closes the window without signing in — false.
/// </summary>
public sealed class VkLoginService
{
    private readonly VkService _vk;

    public VkLoginService(VkService vk) => _vk = vk;

    /// <summary>true — signed in, web session cookies saved to vk_auth.json.</summary>
    public async Task<bool> LoginAsync(Window owner)
    {
        var window = new VkLoginWindow(_vk) { Owner = owner };
        // ShowDialog blocks the UI thread only for its own window — the app keeps running.
        var result = window.ShowDialog() == true;
        await Task.CompletedTask; // async signature reserved (future session confirmation)
        return result;
    }
}
