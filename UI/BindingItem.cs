using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MacroInWZa007.Actions;
using MacroInWZa007.Core;
using MacroInWZa007.Input;

namespace MacroInWZa007.UI;

/// <summary>One row of the bindings list.</summary>
public sealed class BindingItem : INotifyPropertyChanged
{
    private readonly Action<BindingItem> _onToggled;
    private readonly string? _triggerError;
    private readonly string? _actionError;
    private string? _conflict;

    public BindingItem(HotkeyBinding model, Action<BindingItem> onToggled)
    {
        Model = model;
        _onToggled = onToggled;
        KeyCaps = TriggerSpec.ToCaps(model.Trigger, model.Mode);
        AppKey = string.IsNullOrWhiteSpace(model.App) ? null : ForegroundTracker.Normalize(model.App);
        Subtitle = ActionRunner.Describe(model.Action) + (AppKey is null ? "" : $" · {model.App!.Trim()}");
        IsMacro = string.Equals(model.Action.Type, "macro", StringComparison.OrdinalIgnoreCase);
        if (TriggerSpec.TryParse(model, out var trigger, out _triggerError))
            Trigger = trigger;
        _actionError = ActionRunner.Validate(model.Action);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public HotkeyBinding Model { get; }
    public TriggerSpec? Trigger { get; }

    /// <summary>Normalized app name, or null for a global binding.</summary>
    public string? AppKey { get; }

    public bool IsMacro { get; }
    public IReadOnlyList<CapToken> KeyCaps { get; }
    public string Name => string.IsNullOrWhiteSpace(Model.Name) ? Model.Trigger : Model.Name;
    public string Subtitle { get; }

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value)
                return;
            Model.Enabled = value;
            OnPropertyChanged();
            _onToggled(this);
        }
    }

    /// <summary>Set by the view model when another enabled binding uses the same keys.</summary>
    public string? Conflict
    {
        get => _conflict;
        set
        {
            if (_conflict == value)
                return;
            _conflict = value;
            OnPropertyChanged(nameof(Error));
            OnPropertyChanged(nameof(HasError));
        }
    }

    /// <summary>Errors that make the binding unusable no matter what else is configured.</summary>
    public bool IsValid => Trigger is not null && _actionError is null;

    public string? Error => _triggerError ?? _actionError ?? _conflict;
    public bool HasError => Error is not null;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
