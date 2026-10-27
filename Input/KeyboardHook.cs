using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using static Clayboard.Input.NativeMethods;

namespace Clayboard.Input;

/// <summary>
/// Global low-level keyboard hook. Must be installed on a thread that pumps messages
/// (the engine's own hook thread); the handler runs on that thread and must return quickly.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    /// <summary>Return true to swallow the key so no other app sees it.</summary>
    public delegate bool KeyHandler(int vk, bool isDown);

    private readonly KeyHandler _handler;
    // Held in a field so the GC can't collect the delegate while Windows still calls it.
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _handle;

    public KeyboardHook(KeyHandler handler)
    {
        _handler = handler;
        _proc = Callback;
    }

    public void Install()
    {
        if (_handle != IntPtr.Zero)
            return;
        _handle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
            return;
        UnhookWindowsHookEx(_handle);
        _handle = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;

            // Skip keys we injected ourselves so actions can't re-trigger hotkeys.
            if ((isDown || isUp) && data.dwExtraInfo != InputSender.Signature)
            {
                try
                {
                    if (_handler((int)data.vkCode, isDown))
                        return 1;
                }
                catch
                {
                    // An exception escaping here would break input for the whole desktop.
                }
            }
        }
        return CallNextHookEx(_handle, nCode, wParam, lParam);
    }
}
