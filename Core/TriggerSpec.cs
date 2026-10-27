using System;
using System.Collections.Generic;
using System.Linq;
using Clayboard.Localization;

namespace Clayboard.Core;

public enum TriggerMode
{
    Press,
    DoubleTap,
    Hold,
}

/// <summary>One label in a trigger's on-screen form: a keycap, or a word such as "then" or "×2".</summary>
public sealed record CapToken(string Text, bool IsKey);

/// <summary>
/// Parsed form of a binding's trigger: one or more key combos ("Ctrl+K, Ctrl+B" is a sequence)
/// plus how the keys must be pressed.
/// </summary>
public sealed class TriggerSpec
{
    public const int DefaultHoldMs = 500;

    private TriggerSpec(IReadOnlyList<KeyCombo> steps, TriggerMode mode, int holdMs)
    {
        Steps = steps;
        Mode = mode;
        HoldMs = holdMs;
        Id = $"{mode}:{string.Join(",", steps)}";
    }

    public IReadOnlyList<KeyCombo> Steps { get; }
    public TriggerMode Mode { get; }
    public int HoldMs { get; }
    public KeyCombo First => Steps[0];
    public bool IsSequence => Steps.Count > 1;

    /// <summary>Two triggers with the same id react to the same key presses.</summary>
    public string Id { get; }

    public static bool TryParse(HotkeyBinding binding, out TriggerSpec? spec, out string? error) =>
        TryParse(binding.Trigger, binding.Mode, binding.HoldMs, out spec, out error);

    public static bool TryParse(string? trigger, string? mode, int? holdMs, out TriggerSpec? spec, out string? error)
    {
        spec = null;
        if (!TryParseMode(mode, out var parsedMode))
        {
            error = Loc.F("Err.Mode", mode);
            return false;
        }

        var parts = SplitSteps(trigger);
        if (parts.Length == 0)
        {
            error = Loc.T("Err.NoKeysSet");
            return false;
        }
        if (parts.Length > 1 && parsedMode != TriggerMode.Press)
        {
            error = Loc.T("Err.SequenceMode");
            return false;
        }

        var steps = new List<KeyCombo>();
        foreach (var part in parts)
        {
            // A lone modifier can only work as a double-tap or hold: as a press it would fire on every Ctrl+C.
            if (!KeyCombo.TryParse(part, allowModifierKey: parsedMode != TriggerMode.Press, out var combo, out error))
                return false;
            steps.Add(combo);
        }

        if (holdMs is < 100 or > 10000)
        {
            error = Loc.T("Err.HoldMs");
            return false;
        }

        spec = new TriggerSpec(steps, parsedMode, holdMs ?? DefaultHoldMs);
        error = null;
        return true;
    }

    public static bool TryParseMode(string? mode, out TriggerMode result)
    {
        switch ((mode ?? "").Trim().ToLowerInvariant())
        {
            case "":
            case "press":
                result = TriggerMode.Press;
                return true;
            case "doubletap":
            case "double-tap":
                result = TriggerMode.DoubleTap;
                return true;
            case "hold":
                result = TriggerMode.Hold;
                return true;
            default:
                result = TriggerMode.Press;
                return false;
        }
    }

    /// <summary>Keycaps for a trigger as typed, such as [Ctrl][K] then [Ctrl][B], or [Shift] ×2.</summary>
    public static IReadOnlyList<CapToken> ToCaps(string? trigger, string? mode)
    {
        var tokens = new List<CapToken>();
        foreach (var part in SplitSteps(trigger))
        {
            if (tokens.Count > 0)
                tokens.Add(new CapToken(Loc.T("Cap.Then"), IsKey: false));
            tokens.AddRange(KeyCombo.ToCaps(part).Select(cap => new CapToken(cap, IsKey: true)));
        }

        TryParseMode(mode, out var parsedMode);
        if (parsedMode == TriggerMode.DoubleTap)
            tokens.Add(new CapToken("×2", IsKey: false));
        else if (parsedMode == TriggerMode.Hold)
            tokens.Add(new CapToken(Loc.T("Cap.Hold"), IsKey: false));
        return tokens;
    }

    public static string[] SplitSteps(string? trigger) =>
        (trigger ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
