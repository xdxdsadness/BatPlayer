using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BatPlayer.Audio;

namespace BatPlayer.Services;

/// <summary>
/// Windows global hotkeys via RegisterHotKey.
/// Registered on a hidden message-receiver window.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private readonly AudioService _audio;
    private HwndSource? _source;
    private readonly System.Collections.Generic.Dictionary<int, Action> _actions = new();

    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public GlobalHotkeyService(AudioService audio) => _audio = audio;

    public void Initialize(Window owner)
    {
        // Ensure the window has an HWND — defer if not yet created
        var helper = new WindowInteropHelper(owner);
        if (helper.Handle == IntPtr.Zero)
        {
            // Force handle creation
            var h = new HwndSource(new HwndSourceParameters("BatPlayerHotkeys")
            {
                Width = 0, Height = 0, WindowStyle = 0, PositionX = 0, PositionY = 0,
                ParentWindow = IntPtr.Zero
            });
            helper = new WindowInteropHelper(owner);
        }

        var hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero) return; // give up silently

        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(HwndHook);

        // Default: media-key style bindings (Ctrl+Shift+...)
        try
        {
            RegisterHotkey(0, 0x50, 0x06, () => _audio.PlayPauseToggle());   // Ctrl+Shift+P
            RegisterHotkey(1, 0x4E, 0x06, () => _audio.Next());             // Ctrl+Shift+N
            RegisterHotkey(2, 0x42, 0x06, () => _audio.Previous());         // Ctrl+Shift+B
        }
        catch { /* hotkeys may already be registered by another app */ }
    }

    public void RegisterHotkey(int id, int vk, int modifiers, Action action)
    {
        if (_source == null) return;
        if (RegisterHotKey(_source.Handle, id, modifiers, vk))
            _actions[id] = action;
    }

    public void UnregisterAll()
    {
        if (_source == null) return;
        foreach (var id in _actions.Keys)
            UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_actions.TryGetValue(id, out var action))
            {
                action();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(HwndHook);
        _source = null;
    }
}
