using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Clayboard.Config;
using Clayboard.Core;
using Clayboard.Input;
using Clayboard.Localization;

namespace Clayboard.Actions;

/// <summary>Validates, describes, and runs <see cref="ActionSpec"/>s.</summary>
public static class ActionRunner
{
    public const int MaxDelayMs = 60000;

    // How long a program we opened counts as "still starting up" when it has no window yet.
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(15);

    // The program each toggling run last opened, so a second press while it's still loading
    // doesn't open another copy.
    private static readonly ConditionalWeakTable<ActionSpec, Process> Launched = new();

    private static readonly Dictionary<string, (int Vk, string Name)> MediaCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["playPause"] = (0xB3, "playPause"),
        ["next"] = (0xB0, "next"),
        ["previous"] = (0xB1, "previous"),
        ["stop"] = (0xB2, "stop"),
        ["volumeUp"] = (0xAF, "volumeUp"),
        ["volumeDown"] = (0xAE, "volumeDown"),
        ["mute"] = (0xAD, "mute"),
    };

    /// <summary>Returns a message describing what is wrong, or null if the action can run.</summary>
    public static string? Validate(ActionSpec action) => Validate(action, isStep: false);

    private static string? Validate(ActionSpec action, bool isStep) => Normalize(action.Type) switch
    {
        "run" when string.IsNullOrWhiteSpace(action.Path) => Loc.T("Err.RunPath"),
        "run" when action.Toggle == true && ToggleTarget(action) is null => Loc.T("Err.ToggleNeedsExe"),
        "run" => null,
        "close" when string.IsNullOrWhiteSpace(action.Process) => Loc.T("Err.CloseApp"),
        "close" => null,
        "text" when action.Secret is not null => Secrets.TryUnprotect(action.Secret, out _) ? null : Loc.T("Err.SecretUnreadable"),
        "text" when string.IsNullOrEmpty(action.Value) => Loc.T("Err.TextValue"),
        "text" => null,
        "sendkeys" => KeyCombo.TryParse(action.Keys, out _, out var error) ? null : error,
        "media" when !MediaCommands.ContainsKey(action.Command ?? "") =>
            Loc.F("Err.Media", action.Command, string.Join(", ", MediaCommands.Keys)),
        "media" => null,
        "macro" when isStep => Loc.T("Err.NestedMacro"),
        "macro" => ValidateSteps(action.Steps),
        "delay" when !isStep => Loc.T("Err.DelayOutside"),
        "delay" when action.Ms is not (>= 0 and <= MaxDelayMs) => Loc.F("Err.DelayMs", MaxDelayMs),
        "delay" => null,
        _ => Loc.F("Err.ActionType", action.Type),
    };

    private static string? ValidateSteps(List<ActionSpec>? steps)
    {
        if (steps is null || steps.Count == 0)
            return Loc.T("Err.NoSteps");
        for (int i = 0; i < steps.Count; i++)
            if (Validate(steps[i] ?? new ActionSpec { Type = "" }, isStep: true) is { } error)
                return Loc.F("Err.Step", i + 1, error);
        return null;
    }

    /// <summary>Short subtitle for the bindings list, such as "Run · wt.exe".</summary>
    public static string Describe(ActionSpec action) => Normalize(action.Type) switch
    {
        "run" when action.Toggle == true => Loc.F("Desc.Toggle", action.Path),
        "run" => Loc.F("Desc.Run", action.Path),
        "close" => Loc.F("Desc.Close", action.Process),
        "text" when action.Secret is not null => Loc.T("Desc.TextSecret"),
        "text" => Loc.T("Desc.Text"),
        "sendkeys" => Loc.F("Desc.Keys", action.Keys),
        "media" => MediaCommands.ContainsKey(action.Command ?? "")
            ? Loc.F("Desc.Media", MediaLabel(action.Command))
            : Loc.T("Desc.MediaPlain"),
        "macro" => action.Steps?.Count == 1 ? Loc.T("Desc.Macro1") : Loc.F("Desc.MacroN", action.Steps?.Count ?? 0),
        "delay" => Loc.F("Desc.Wait", action.Ms),
        _ => action.Type,
    };

    public static string MediaLabel(string? command) =>
        MediaCommands.TryGetValue(command ?? "", out var m) ? Loc.T("Media." + m.Name) : command ?? "";

    /// <summary>
    /// The app a toggling run closes: the explicit <see cref="ActionSpec.Process"/>, else the program
    /// itself when <see cref="ActionSpec.Path"/> names an exe. Null for documents, folders, and URLs.
    /// </summary>
    public static string? ToggleTarget(ActionSpec action)
    {
        if (!string.IsNullOrWhiteSpace(action.Process))
            return action.Process.Trim();
        var path = Environment.ExpandEnvironmentVariables(action.Path ?? "").Trim().Trim('"');
        if (path.Contains("://", StringComparison.Ordinal))
            return null;
        var extension = System.IO.Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || (extension.Length == 0 && path.Length > 0)
            ? path
            : null;
    }

    /// <summary>True for actions that type into whichever window has focus.</summary>
    public static bool TypesIntoFocusedWindow(ActionSpec action) =>
        Normalize(action.Type) is "text" or "sendkeys" or "macro";

    /// <summary>Runs the action. Blocks while it types or waits, so call it off the UI thread.</summary>
    public static void Run(ActionSpec action)
    {
        if (Validate(action) is { } error)
            throw new InvalidOperationException(error);

        // Wait once up front so the trigger's own Ctrl/Alt don't combine with what we type.
        if (TypesIntoFocusedWindow(action))
            InputSender.WaitForModifiersReleased();

        if (Normalize(action.Type) == "macro")
        {
            foreach (var step in action.Steps!)
                RunOne(step);
        }
        else
        {
            RunOne(action);
        }
    }

    private static void RunOne(ActionSpec action)
    {
        switch (Normalize(action.Type))
        {
            case "run":
                if (action.Toggle == true)
                {
                    RunToggle(action);
                    break;
                }
                Start(action);
                break;
            case "close":
                WindowCloser.Close(action.Process!); // Nothing open is fine: there's nothing to close.
                break;
            case "text":
                InputSender.SendText(TextOf(action));
                break;
            case "sendkeys":
                KeyCombo.TryParse(action.Keys, out var combo, out _);
                InputSender.SendCombo(combo);
                break;
            case "media":
                InputSender.SendKey(MediaCommands[action.Command!].Vk);
                break;
            case "delay":
                Thread.Sleep(action.Ms!.Value);
                break;
        }
    }

    /// <summary>Closes the program when it's open, otherwise opens it.</summary>
    private static void RunToggle(ActionSpec action)
    {
        if (WindowCloser.Close(ToggleTarget(action)!) > 0)
            return;
        lock (Launched)
        {
            if (Launched.TryGetValue(action, out var previous) && IsStillStarting(previous))
                return; // Opened moments ago and no window yet: a second press shouldn't open another.
            var started = Start(action);
            if (started is null)
                Launched.Remove(action); // Handed to an already-running process; nothing to track.
            else
                Launched.AddOrUpdate(action, started);
            previous?.Dispose();
        }
    }

    private static bool IsStillStarting(Process process)
    {
        try
        {
            return !process.HasExited && DateTime.Now - process.StartTime < StartupGrace;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>The text to type: the decrypted secret if there is one, otherwise the plain value.</summary>
    public static string TextOf(ActionSpec action) =>
        action.Secret is not null && Secrets.TryUnprotect(action.Secret, out var secret) ? secret : action.Value ?? "";

    private static Process? Start(ActionSpec action)
    {
        var path = Environment.ExpandEnvironmentVariables(action.Path!);
        if (!ShellLauncher.IsElevated)
            return Process.Start(new ProcessStartInfo(path) { Arguments = action.Args ?? "", UseShellExecute = true });

        // Running as admin: never pass that on to programs named in config.json.
        try
        {
            ShellLauncher.OpenAsUser(path, action.Args ?? "");
            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            throw new InvalidOperationException(Loc.F("Err.Unelevated", ex.Message), ex);
        }
    }

    private static string Normalize(string? type) => (type ?? "").Trim().ToLowerInvariant();
}
