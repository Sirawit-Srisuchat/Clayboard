using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacroInWZa007.Input;

/// <summary>
/// Keeps the process name of the foreground window up to date, so the keyboard hook can
/// check per-app bindings without doing any lookups itself.
/// </summary>
public sealed class ForegroundTracker : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint eventThread, uint eventTime);

    // Held in a field so the GC can't collect the delegate while Windows still calls it.
    private readonly WinEventProc _proc;
    private IntPtr _hook;
    private volatile string _currentApp = "";

    public ForegroundTracker()
    {
        _proc = (_, _, hwnd, _, _, _, _) => Update(hwnd);
    }

    /// <summary>Normalized name (see <see cref="Normalize"/>) of the app in front.</summary>
    public string CurrentApp => _currentApp;

    /// <summary>Starts tracking. Call on a thread that pumps messages.</summary>
    public void Start()
    {
        if (_hook != IntPtr.Zero)
            return;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0,
            WINEVENT_OUTOFCONTEXT);
        Update(GetForegroundWindow());
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero)
            return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>"Chrome.exe", "chrome" and " chrome.EXE " all become "chrome".</summary>
    public static string Normalize(string? app)
    {
        var name = (app ?? "").Trim().ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }

    private void Update(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            _currentApp = Normalize(process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _currentApp = ""; // The process already exited.
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc proc, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
