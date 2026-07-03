using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Win32;

namespace AgentSignal.App.Services;

/// <summary>
/// System-wide Ctrl+Alt+R → manual reset, one interface with a per-OS implementation (the
/// StartupManager pattern). Register/Unregister are idempotent; the App unregisters while the
/// Settings window is open (its Reset button covers that case) and re-registers on close.
/// </summary>
public interface IGlobalHotkey
{
    /// <summary>Claim the hotkey with the OS. Returns false when unsupported or already taken.</summary>
    bool Register();
    void Unregister();
}

public static class GlobalHotkey
{
    /// <summary>Ctrl+Alt+R for <paramref name="window"/>'s lifetime. Windows is implemented; others no-op.</summary>
    public static IGlobalHotkey Create(Window window, Action onPressed) =>
        OperatingSystem.IsWindows() ? new WindowsGlobalHotkey(window, onPressed) : new NoopGlobalHotkey();
}

/// <summary>Non-Windows placeholder: X11/Wayland and macOS global shortcuts come with Phase 7+.</summary>
internal sealed class NoopGlobalHotkey : IGlobalHotkey
{
    public bool Register() => false;
    public void Unregister() { }
}

/// <summary>
/// Windows: RegisterHotKey(Ctrl+Alt+R) against the widget window's HWND, with the WM_HOTKEY message
/// picked out of its message loop via Avalonia's WndProc hook. WM_HOTKEY arrives on the UI thread,
/// so the callback may touch view models directly. MOD_NOREPEAT stops a held key from refiring.
/// </summary>
internal sealed class WindowsGlobalHotkey : IGlobalHotkey
{
    private const int HotkeyId = 0xA515;         // app-unique id within our window
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkR = 0x52;
    private const uint WmHotkey = 0x0312;

    private readonly Window _window;
    private readonly Action _onPressed;
    private nint _hwnd;
    private bool _hooked;
    private bool _registered;

    public WindowsGlobalHotkey(Window window, Action onPressed)
    {
        _window = window;
        _onPressed = onPressed;
    }

    public bool Register()
    {
        if (_registered) return true;

        _hwnd = _window.TryGetPlatformHandle()?.Handle ?? 0;
        if (_hwnd == 0) return false;

        // The message hook stays attached for the window's lifetime (a cheap msg check); only the
        // OS-level hotkey claim toggles with Register/Unregister.
        if (!_hooked)
        {
            Win32Properties.AddWndProcHookCallback(_window, WndProcHook);
            _hooked = true;
        }

        _registered = RegisterHotKey(_hwnd, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkR);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
    }

    private nint WndProcHook(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam == HotkeyId && _registered)
        {
            handled = true;
            _onPressed();
        }
        return 0;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
