using System;
using System.IO;
using Microsoft.Win32;

namespace MacroInWZa007.Config;

/// <summary>Turns "start with Windows" on or off through the current user's Run key.</summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MacroInWZa007";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Re-points the entry at this exe only when the registered one no longer exists (the app was moved).
    /// Opening some other copy once, say from Downloads or a USB stick, must not take over startup.
    /// </summary>
    public static void RefreshPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        if (key?.GetValue(ValueName) is not string command)
            return;
        int closingQuote = command.StartsWith('"') ? command.IndexOf('"', 1) : -1;
        var registered = closingQuote > 0 ? command[1..closingQuote] : command.Split(' ')[0];
        if (!File.Exists(registered))
            SetEnabled(true);
    }
}
