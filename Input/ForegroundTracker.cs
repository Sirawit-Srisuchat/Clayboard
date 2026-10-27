using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

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
        _currentApp = Normalize(Path.GetFileName(ImagePathOf(pid) ?? ""));
    }

    /// <summary>
    /// Full exe path of a process, or null if it exited or is off limits. One cheap kernel call,
    /// unlike <see cref="Process"/>, which gathers details about every process to answer.
    /// </summary>
    public static string? ImagePathOf(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

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
