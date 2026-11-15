using System.Runtime.InteropServices;
using System.Windows.Forms;
using MistyStep.Core.Settings;

namespace MistyStep.Windows.Shell;

/// <summary>
/// One system-wide hotkey through RegisterHotKey on a message-only window (create on the UI thread).
/// <see cref="TryRegister"/> returns false when another app already owns the combination.
/// </summary>
public sealed partial class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x4D53; // "MS"
    private const uint MOD_NOREPEAT = 0x4000;
    private static readonly nint HWND_MESSAGE = -3;

    private readonly HotkeyWindow _window;
    private bool _registered;

    public GlobalHotkey()
    {
        _window = new HotkeyWindow(this);
        _window.CreateHandle(new CreateParams { Caption = "MistyStep.Hotkey", Parent = HWND_MESSAGE });
    }

    public event Action? Pressed;

    /// <summary>The registered hotkey, or null when none is registered.</summary>
    public Hotkey? Current { get; private set; }

    /// <summary>Replaces any registered hotkey. False if the combination is taken (nothing is registered then).</summary>
    public bool TryRegister(Hotkey hotkey)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        Unregister();
        if (!RegisterHotKey(_window.Handle, HotkeyId, (uint)hotkey.Modifiers | MOD_NOREPEAT, (uint)hotkey.VirtualKey)) return false;
        _registered = true;
        Current = hotkey;
        return true;
    }

    public void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(_window.Handle, HotkeyId);
        _registered = false;
        Current = null;
    }

    public void Dispose()
    {
        Unregister();
        _window.DestroyHandle();
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hWnd, int id);

    private sealed class HotkeyWindow(GlobalHotkey owner) : NativeWindow
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam == HotkeyId)
            {
                owner.Pressed?.Invoke();
                return;
            }

            base.WndProc(ref m);
        }
    }
}
