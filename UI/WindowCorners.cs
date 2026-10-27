using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Clayboard.UI;

/// <summary>Asks Windows 11 to round a borderless window's corners (ignored on Windows 10).</summary>
internal static class WindowCorners
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public static void Round(Window window)
    {
        int preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, DWMWA_WINDOW_CORNER_PREFERENCE,
            ref preference, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
