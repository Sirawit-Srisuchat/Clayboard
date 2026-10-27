using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Clayboard.Input;

/// <summary>
/// Finds and closes an app's windows the polite way, by sending WM_CLOSE as the × button does,
/// so apps with unsaved work still get to ask about it.
/// </summary>
public static class WindowCloser
{
    private const uint WM_CLOSE = 0x0010;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    public static bool HasWindows(string app) => FindWindows(app).Count > 0;

    /// <summary>Asks every main window of <paramref name="app"/> to close. Returns how many were asked.</summary>
    public static int Close(string app)
    {
        var windows = FindWindows(app);
        foreach (var hwnd in windows)
            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return windows.Count;
    }

    /// <summary>The process name an app setting refers to: "C:\x\Notepad.exe" and "notepad" both give "notepad".</summary>
    public static string ProcessNameOf(string app) =>
        ForegroundTracker.Normalize(System.IO.Path.GetFileName(app.Trim().Trim('"')));

    private static List<IntPtr> FindWindows(string app)
    {
        var name = ProcessNameOf(app);
        // A full path ("C:\Tools\app.exe") pins one program; a bare name matches any exe called that.
        var path = app.Trim().Trim('"');
        bool byPath = path.Contains('\\');
        var pids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                if (!byPath || string.Equals(ForegroundTracker.ImagePathOf((uint)process.Id), path,
                        StringComparison.OrdinalIgnoreCase))
                    pids.Add((uint)process.Id);
            }
        }
        pids.Remove((uint)Environment.ProcessId); // Never close ourselves.

        var windows = new List<IntPtr>();
        if (pids.Count == 0)
            return windows;

        EnumWindows((hwnd, _) =>
        {
            // Only real top-level windows: visible, titled, not owned by another window, not cloaked.
            if (IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero && GetWindowTextLength(hwnd) > 0
                && !IsCloaked(hwnd))
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pids.Contains(pid))
                    windows.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    // Suspended Store apps and windows on other virtual desktops are "cloaked": present but not shown.
    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
