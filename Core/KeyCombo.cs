using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Clayboard.Localization;

namespace Clayboard.Core;

[Flags]
public enum Mods
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A set of modifiers plus one main virtual key.</summary>
public readonly record struct KeyCombo(Mods Mods, int Vk)
{
    private static readonly Dictionary<string, Mods> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = Mods.Ctrl,
        ["Control"] = Mods.Ctrl,
        ["Alt"] = Mods.Alt,
        ["Shift"] = Mods.Shift,
        ["Win"] = Mods.Win,
        ["Windows"] = Mods.Win,
    };

    private static readonly Dictionary<string, Key> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Esc"] = Key.Escape,
        ["Enter"] = Key.Return,
        ["Del"] = Key.Delete,
        ["Ins"] = Key.Insert,
        ["PgUp"] = Key.PageUp,
        ["PgDn"] = Key.PageDown,
        ["Caps"] = Key.CapsLock,
        ["Backspace"] = Key.Back,
        ["PrtSc"] = Key.PrintScreen,
        ["Menu"] = Key.Apps,
        ["PlayPause"] = Key.MediaPlayPause,
        ["`"] = Key.Oem3,
        ["-"] = Key.OemMinus,
        ["="] = Key.OemPlus,
        ["["] = Key.OemOpenBrackets,
        ["]"] = Key.OemCloseBrackets,
        ["\\"] = Key.Oem5,
        [";"] = Key.OemSemicolon,
        ["'"] = Key.OemQuotes,
        [","] = Key.OemComma,
        ["."] = Key.OemPeriod,
        ["/"] = Key.OemQuestion,
    };

    public static bool TryParse(string? text, out KeyCombo combo, out string? error) =>
        TryParse(text, allowModifierKey: false, out combo, out error);

    /// <param name="allowModifierKey">
    /// Accept combos made only of modifiers ("Shift", "Ctrl+Shift"); the last modifier becomes the main key.
    /// </param>
    public static bool TryParse(string? text, bool allowModifierKey, out KeyCombo combo, out string? error)
    {
        combo = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = Loc.T("Err.NoKeysSet");
            return false;
        }

        var mods = Mods.None;
        var lastMod = Mods.None;
        int vk = 0;
        foreach (var part in Split(text))
        {
            if (ModifierNames.TryGetValue(part, out var mod))
            {
                mods |= mod;
                lastMod = mod;
                continue;
            }
            if (vk != 0)
            {
                error = Loc.F("Err.OneKey", text);
                return false;
            }
            if (!TryGetVk(part, out vk))
            {
                error = Loc.F("Err.UnknownKey", part);
                return false;
            }
        }

        if (vk == 0 && allowModifierKey && lastMod != Mods.None)
        {
            vk = GenericVk(lastMod);
            mods &= ~lastMod;
        }
        if (vk == 0)
        {
            error = Loc.T("Err.NeedKey");
            return false;
        }
        combo = new KeyCombo(mods, vk);
        return true;
    }

    public override string ToString() => Format(Mods, KeyName(Vk) ?? $"vk{Vk}");

    /// <summary>Builds "Ctrl+Alt+T"; with a null key, only the modifiers.</summary>
    public static string Format(Mods mods, string? key)
    {
        var parts = new List<string>();
        foreach (var mod in new[] { Mods.Ctrl, Mods.Alt, Mods.Shift, Mods.Win })
            if (mods.HasFlag(mod))
                parts.Add(mod.ToString());
        if (key is not null)
            parts.Add(key);
        return string.Join("+", parts);
    }

    /// <summary>Name for a virtual key that <see cref="TryParse"/> accepts, or null if it has none.</summary>
    public static string? KeyName(int vk)
    {
        var mod = ModifierOf(vk);
        if (mod != Mods.None)
            return mod.ToString();

        var key = KeyInterop.KeyFromVirtualKey(vk);
        if (key == Key.None)
            return null;
        if (key >= Key.D0 && key <= Key.D9)
            return ((char)('0' + (key - Key.D0))).ToString();
        foreach (var (alias, aliasKey) in KeyAliases)
            if (aliasKey == key && alias.Length == 1)
                return alias;

        // Several Key members share a value (Return/Enter, Capital/CapsLock), so name those explicitly.
        return key switch
        {
            Key.Return => "Enter",
            Key.Escape => "Esc",
            Key.CapsLock => "CapsLock",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Back => "Backspace",
            Key.PrintScreen => "PrintScreen",
            _ => key.ToString(),
        };
    }

    public static Mods ModifierOf(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => Mods.Shift,
        0x11 or 0xA2 or 0xA3 => Mods.Ctrl,
        0x12 or 0xA4 or 0xA5 => Mods.Alt,
        0x5B or 0x5C => Mods.Win,
        _ => Mods.None,
    };

    /// <summary>Splits a combo into display labels, with modifier names normalized.</summary>
    public static IReadOnlyList<string> ToCaps(string? text) =>
        Split(text ?? "")
            .Select(p => ModifierNames.TryGetValue(p, out var m) ? m.ToString() : p)
            .ToList();

    /// <summary>Side-neutral virtual key for a modifier, so left and right Shift match the same binding.</summary>
    public static int GenericVk(Mods mod) => mod switch
    {
        Mods.Shift => 0x10,
        Mods.Ctrl => 0x11,
        Mods.Alt => 0x12,
        Mods.Win => 0x5B,
        _ => 0,
    };

    public static bool IsModifierVk(int vk) => ModifierOf(vk) != Mods.None;

    private static string[] Split(string text) =>
        text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool TryGetVk(string name, out int vk)
    {
        vk = 0;
        Key key;
        if (name.Length == 1 && char.IsAsciiDigit(name[0]))
            key = Key.D0 + (name[0] - '0');
        else if (!KeyAliases.TryGetValue(name, out key))
        {
            // Enum.TryParse also accepts numbers and comma lists, so guard against both.
            if (name.Contains(',') || int.TryParse(name, out _) || !Enum.TryParse(name, true, out key))
                return false;
        }

        if (key == Key.None || !Enum.IsDefined(key))
            return false;
        vk = KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }
}
