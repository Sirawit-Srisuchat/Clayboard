using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using MacroInWZa007.Core;
using MacroInWZa007.Localization;
using static MacroInWZa007.Input.NativeMethods;

namespace MacroInWZa007.Input;

/// <summary>Synthesizes keyboard input with SendInput.</summary>
public static class InputSender
{
    /// <summary>Tag on every event we inject, so our own hook can ignore it.</summary>
    public static readonly IntPtr Signature = new(0x4D57A007);

    // Unassigned virtual key, used to stop a lone Alt/Win release from opening a menu.
    private const int VK_MASK = 0xE8;

    private static readonly HashSet<int> ExtendedKeys = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp, PgDn, End, Home, arrows
        0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90, 0xA3, 0xA5, // Ins, Del, Win, Apps, NumDivide, NumLock, RCtrl, RAlt
        0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3, // volume and media keys
    };

    private static readonly (Mods Mod, int Vk)[] ModifierKeys =
    {
        (Mods.Ctrl, VK_CONTROL),
        (Mods.Alt, VK_MENU),
        (Mods.Shift, VK_SHIFT),
        (Mods.Win, VK_LWIN),
    };

    public static void SendCombo(KeyCombo combo)
    {
        var inputs = new List<INPUT>();
        foreach (var (mod, vk) in ModifierKeys)
            if (combo.Mods.HasFlag(mod))
                inputs.Add(Key(vk, up: false));
        inputs.Add(Key(combo.Vk, up: false));
        inputs.Add(Key(combo.Vk, up: true));
        for (int i = ModifierKeys.Length - 1; i >= 0; i--)
            if (combo.Mods.HasFlag(ModifierKeys[i].Mod))
                inputs.Add(Key(ModifierKeys[i].Vk, up: true));
        Send(inputs);
    }

    public static void SendKey(int vk) => Send(new List<INPUT> { Key(vk, false), Key(vk, true) });

    public static void SendText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (char c in text)
        {
            switch (c)
            {
                case '\r':
                    continue;
                case '\n':
                    inputs.Add(Key(0x0D, false));
                    inputs.Add(Key(0x0D, true));
                    break;
                case '\t':
                    inputs.Add(Key(0x09, false));
                    inputs.Add(Key(0x09, true));
                    break;
                default:
                    inputs.Add(Unicode(c, up: false));
                    inputs.Add(Unicode(c, up: true));
                    break;
            }
        }
        Send(inputs);
    }

    public static void SendMaskKey() => SendKey(VK_MASK);

    public static void SendKeyDown(int vk) => Send(new List<INPUT> { Key(vk, false) });

    /// <summary>
    /// Waits until the user lets go of Ctrl/Alt/Shift/Win, so the keys of the trigger
    /// don't combine with what we are about to type.
    /// </summary>
    public static void WaitForModifiersReleased(int timeoutMs = 1500)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs &&
               (IsDown(VK_CONTROL) || IsDown(VK_MENU) || IsDown(VK_SHIFT) || IsDown(VK_LWIN) || IsDown(VK_RWIN)))
        {
            Thread.Sleep(10);
        }
    }

    public static Mods CurrentMods()
    {
        var mods = Mods.None;
        if (IsDown(VK_CONTROL)) mods |= Mods.Ctrl;
        if (IsDown(VK_MENU)) mods |= Mods.Alt;
        if (IsDown(VK_SHIFT)) mods |= Mods.Shift;
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) mods |= Mods.Win;
        return mods;
    }

    private static INPUT Key(int vk, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (ExtendedKeys.Contains(vk))
            flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = flags, dwExtraInfo = Signature } },
        };
    }

    private static INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0),
                dwExtraInfo = Signature,
            },
        },
    };

    private static void Send(List<INPUT> inputs)
    {
        if (inputs.Count == 0)
            return;
        var array = inputs.ToArray();
        uint sent = SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());
        if (sent != array.Length)
            throw new InvalidOperationException(Loc.T("Err.Blocked"));
    }
}
