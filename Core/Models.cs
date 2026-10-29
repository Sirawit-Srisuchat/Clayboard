using System;
using System.Collections.Generic;

namespace Clayboard.Core;

public sealed class AppConfig
{
    /// <summary>UI language code (en, th, ja, ko, zh, ru); null follows the Windows display language.</summary>
    public string? Language { get; set; }

    /// <summary>Color theme key (see UI.Palette); null is the original earth-tone theme, "system" follows Windows.</summary>
    public string? Theme { get; set; }

    /// <summary>How long a sequence waits for its next step, in milliseconds; null means the default.</summary>
    public int? SequenceTimeoutMs { get; set; }

    /// <summary>Group names in display order. The order of <see cref="Bindings"/> is the order within each group.</summary>
    public List<string> Groups { get; set; } = new();

    /// <summary>Groups shown folded up in the list.</summary>
    public List<string> CollapsedGroups { get; set; } = new();

    /// <summary>List order: null or "manual" (the user's own), "name", "nameDesc", "keys", or "type".</summary>
    public string? Sort { get; set; }

    /// <summary>Which bindings the list shows: null or "all", "on", "off", or "problems".</summary>
    public string? Show { get; set; }

    public List<HotkeyBinding> Bindings { get; set; } = new();
}

/// <summary>A file of bindings made by Export, for backups and moving to another PC.</summary>
public sealed class BindingsFile
{
    public string Format { get; set; } = "clayboard-bindings";
    public int Version { get; set; } = 1;
    public DateTime ExportedAt { get; set; } = DateTime.Now;

    /// <summary>Group order, so an import keeps the groups in the same order.</summary>
    public List<string> Groups { get; set; } = new();

    public List<HotkeyBinding> Bindings { get; set; } = new();
}

public sealed class HotkeyBinding
{
    public string Name { get; set; } = "";

    /// <summary>
    /// Key combo such as "Ctrl+Alt+T" or "CapsLock". Several combos separated by commas
    /// ("Ctrl+K, Ctrl+B") must be pressed one after another.
    /// </summary>
    public string Trigger { get; set; } = "";

    /// <summary>"press" (the default when null), "doubleTap", or "hold".</summary>
    public string? Mode { get; set; }

    /// <summary>How long a "hold" trigger must be held, in milliseconds.</summary>
    public int? HoldMs { get; set; }

    /// <summary>Process name such as "chrome.exe"; the binding then only works while that app is in front.</summary>
    public string? App { get; set; }

    /// <summary>Name of the group the binding is filed under in the list; null for none. Doesn't affect behavior.</summary>
    public string? Group { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>When true the trigger key never reaches the focused app.</summary>
    public bool Suppress { get; set; } = true;

    public ActionSpec Action { get; set; } = new();
}

/// <summary>
/// One action. Which fields matter depends on <see cref="Type"/>:
/// run (path, args, toggle, process), close (process), text (value), sendKeys (keys),
/// media (command), macro (steps), and delay (ms, only inside a macro).
/// </summary>
public sealed class ActionSpec
{
    public string Type { get; set; } = "run";
    public string? Path { get; set; }
    public string? Args { get; set; }
    public string? Value { get; set; }
    public string? Keys { get; set; }
    public string? Command { get; set; }
    public List<ActionSpec>? Steps { get; set; }
    public int? Ms { get; set; }

    /// <summary>For text: the text encrypted with DPAPI (see Config.Secrets), used instead of <see cref="Value"/>.</summary>
    public string? Secret { get; set; }

    /// <summary>For run: when the program is already open, the same keys close it instead.</summary>
    public bool? Toggle { get; set; }

    /// <summary>
    /// App to close, such as "notepad.exe": the target of close, and for a toggling run
    /// the app to look for when it isn't the program in <see cref="Path"/> (wt.exe opens WindowsTerminal.exe).
    /// </summary>
    public string? Process { get; set; }
}
