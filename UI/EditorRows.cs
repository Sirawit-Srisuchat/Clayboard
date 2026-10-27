using System.ComponentModel;
using System.Runtime.CompilerServices;
using Clayboard.Actions;
using Clayboard.Core;
using Clayboard.Localization;

namespace Clayboard.UI;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A group's title row in the bindings list. Its <see cref="Name"/> is null for bindings in no group.</summary>
public sealed class GroupHeader : Observable
{
    private int _count;
    private bool _isCollapsed;

    public GroupHeader(string? name)
    {
        Name = name;
    }

    public string? Name { get; }
    public bool IsNamed => Name is not null;
    public string Display => Name ?? Loc.T("Group.None");

    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }

    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            Set(ref _isCollapsed, value);
            Raise(nameof(Chevron));
        }
    }

    /// <summary>Segoe Fluent chevron: right when folded, down when open.</summary>
    public string Chevron => IsCollapsed ? "" : "";

    public void RefreshText() => Raise(nameof(Display));
}

/// <summary>One editable step of a macro.</summary>
public sealed class MacroStep : Observable
{
    private int _number;
    private string _keys = "";
    private string _text = "";
    private string _ms = "100";
    private string _path = "";

    public MacroStep(string type)
    {
        Type = type;
    }

    /// <summary>sendKeys, text, delay, run, close, or media (media steps only come from config.json).</summary>
    public string Type { get; }

    public string Label => Loc.T(Type switch
    {
        "sendKeys" => "Step.Keys",
        "text" => "Step.Type",
        "delay" => "Step.Wait",
        "run" => "Step.Run",
        "close" => "Step.Close",
        _ => "Step.Media",
    });

    public int Number
    {
        get => _number;
        set => Set(ref _number, value);
    }

    public string Keys
    {
        get => _keys;
        set => Set(ref _keys, value);
    }

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    public string Ms
    {
        get => _ms;
        set => Set(ref _ms, value);
    }

    public string Path
    {
        get => _path;
        set => Set(ref _path, value);
    }

    public string? Command { get; init; }

    /// <summary>Kept from config.json; the step row has no field for it.</summary>
    public string? Args { get; init; }

    public string CommandLabel => ActionRunner.MediaLabel(Command);

    public static MacroStep From(ActionSpec spec)
    {
        string type = (spec.Type ?? "").Trim().ToLowerInvariant() switch
        {
            "sendkeys" => "sendKeys",
            "text" => "text",
            "delay" => "delay",
            "run" => "run",
            "close" => "close",
            _ => "media",
        };
        return new MacroStep(type)
        {
            Keys = spec.Keys ?? "",
            Text = spec.Value ?? "",
            Ms = spec.Ms?.ToString() ?? "100",
            Path = (type == "close" ? spec.Process : spec.Path) ?? "",
            Command = spec.Command,
            Args = spec.Args,
        };
    }

    public ActionSpec ToSpec() => Type switch
    {
        "sendKeys" => new ActionSpec { Type = "sendKeys", Keys = Keys },
        "text" => new ActionSpec { Type = "text", Value = Text.Replace("\r\n", "\n") },
        // An unparseable number becomes -1 so validation reports it.
        "delay" => new ActionSpec { Type = "delay", Ms = int.TryParse(Ms.Trim(), out int ms) ? ms : -1 },
        "run" => new ActionSpec { Type = "run", Path = Path.Trim(), Args = Args },
        "close" => new ActionSpec { Type = "close", Process = Path.Trim() },
        _ => new ActionSpec { Type = "media", Command = Command },
    };
}
