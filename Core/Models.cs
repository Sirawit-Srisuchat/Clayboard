using System.Collections.Generic;

namespace MacroInWZa007.Core;

public sealed class AppConfig
{
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

    public bool Enabled { get; set; } = true;

    /// <summary>When true the trigger key never reaches the focused app.</summary>
    public bool Suppress { get; set; } = true;

    public ActionSpec Action { get; set; } = new();
}

/// <summary>
/// One action. Which fields matter depends on <see cref="Type"/>:
/// run (path, args), text (value), sendKeys (keys), media (command),
/// macro (steps), and delay (ms, only inside a macro).
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
}
