using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using MacroInWZa007.Core;
using MacroInWZa007.Input;

namespace MacroInWZa007.Actions;

/// <summary>Validates, describes, and runs <see cref="ActionSpec"/>s.</summary>
public static class ActionRunner
{
    public const int MaxDelayMs = 60000;

    private static readonly Dictionary<string, (int Vk, string Label)> MediaCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["playPause"] = (0xB3, "Play / pause"),
        ["next"] = (0xB0, "Next track"),
        ["previous"] = (0xB1, "Previous track"),
        ["stop"] = (0xB2, "Stop"),
        ["volumeUp"] = (0xAF, "Volume up"),
        ["volumeDown"] = (0xAE, "Volume down"),
        ["mute"] = (0xAD, "Mute"),
    };

    /// <summary>Returns a message describing what is wrong, or null if the action can run.</summary>
    public static string? Validate(ActionSpec action) => Validate(action, isStep: false);

    private static string? Validate(ActionSpec action, bool isStep) => Normalize(action.Type) switch
    {
        "run" when string.IsNullOrWhiteSpace(action.Path) => "Run needs a \"path\".",
        "run" => null,
        "text" when string.IsNullOrEmpty(action.Value) => "Text needs a \"value\".",
        "text" => null,
        "sendkeys" => KeyCombo.TryParse(action.Keys, out _, out var error) ? null : error,
        "media" when !MediaCommands.ContainsKey(action.Command ?? "") =>
            $"Unknown media command \"{action.Command}\". Use {string.Join(", ", MediaCommands.Keys)}.",
        "media" => null,
        "macro" when isStep => "A macro can't contain another macro.",
        "macro" => ValidateSteps(action.Steps),
        "delay" when !isStep => "Wait only works as a macro step.",
        "delay" when action.Ms is not (>= 0 and <= MaxDelayMs) => $"Wait needs \"ms\" between 0 and {MaxDelayMs}.",
        "delay" => null,
        _ => $"Unknown action type \"{action.Type}\".",
    };

    private static string? ValidateSteps(List<ActionSpec>? steps)
    {
        if (steps is null || steps.Count == 0)
            return "Add at least one step to the macro.";
        for (int i = 0; i < steps.Count; i++)
            if (Validate(steps[i] ?? new ActionSpec { Type = "" }, isStep: true) is { } error)
                return $"Step {i + 1}: {error}";
        return null;
    }

    /// <summary>Short subtitle for the bindings list, such as "Run · wt.exe".</summary>
    public static string Describe(ActionSpec action) => Normalize(action.Type) switch
    {
        "run" => $"Run · {action.Path}",
        "text" => "Text",
        "sendkeys" => $"Keys · {action.Keys}",
        "media" => MediaCommands.TryGetValue(action.Command ?? "", out var m) ? $"Media · {m.Label}" : "Media",
        "macro" => action.Steps?.Count == 1 ? "Macro · 1 step" : $"Macro · {action.Steps?.Count ?? 0} steps",
        "delay" => $"Wait {action.Ms} ms",
        _ => action.Type,
    };

    public static string MediaLabel(string? command) =>
        MediaCommands.TryGetValue(command ?? "", out var m) ? m.Label : command ?? "";

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
                Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(action.Path!))
                {
                    Arguments = action.Args ?? "",
                    UseShellExecute = true,
                });
                break;
            case "text":
                InputSender.SendText(action.Value!);
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

    private static string Normalize(string? type) => (type ?? "").Trim().ToLowerInvariant();
}
