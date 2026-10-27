using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Data;
using Clayboard.Config;
using Clayboard.Core;
using Clayboard.Input;
using Clayboard.Localization;

namespace Clayboard.UI;

/// <summary>Which bindings the list shows.</summary>
public enum ListScope
{
    All,
    Macros,
    Profile,
}

/// <summary>A group of bindings that share an app ("Global" when they work everywhere).</summary>
public sealed record ProfileSummary(string? AppKey, string Name, int Count)
{
    public string CountText => Count == 1 ? Loc.T("Profiles.Count1") : Loc.F("Profiles.CountN", Count);
    public string Hint => Loc.T(AppKey is null ? "Profiles.GlobalHint" : "Profiles.AppHint");
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ConfigStore _store;
    private readonly HotkeyEngine _engine;
    private readonly TrustStore _trust;
    private AppConfig _config = new();
    private string _searchText = "";
    private string? _loadError;
    private ListScope _scope = ListScope.All;
    private string? _profileKey;
    private string _profileName = "";

    public MainViewModel(ConfigStore store, HotkeyEngine engine, TrustStore trust)
    {
        _store = store;
        _engine = engine;
        _trust = trust;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = Matches;
        Loc.Changed += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<bool>? PausedChanged;

    public ObservableCollection<BindingItem> Items { get; } = new();
    public ObservableCollection<ProfileSummary> Profiles { get; } = new();
    public ICollectionView View { get; }
    public HotkeyEngine Engine => _engine;
    public string ConfigPath => _store.FilePath;
    public string Language => Loc.Language;

    public int SequenceTimeoutMs => _engine.SequenceTimeoutMs;

    public int UntrustedCount => Items.Count(i => !i.IsTrusted);
    public bool HasUntrusted => UntrustedCount > 0;
    public string TrustBanner => Loc.F("Trust.Banner", string.Join(", ", Items.Where(i => !i.IsTrusted).Select(i => i.Name)));

    /// <summary>Approves every binding that was changed outside the app, so it can run.</summary>
    public void AllowUntrusted()
    {
        _trust.Trust(Items.Where(i => !i.IsTrusted).Select(i => i.Model));
        RebuildItems();
    }

    /// <summary>Sets how long sequences wait for their next step, clamped to a usable range, and saves it.</summary>
    public int SetSequenceTimeout(int ms)
    {
        ms = Math.Clamp(ms, HotkeyEngine.MinSequenceTimeoutMs, HotkeyEngine.MaxSequenceTimeoutMs);
        _config.SequenceTimeoutMs = ms;
        _engine.SequenceTimeoutMs = ms;
        if (!TrySave(out var error))
            LoadError = error;
        return ms;
    }

    /// <summary>Switches the UI language and remembers it in config.json.</summary>
    public void SetLanguage(string code)
    {
        if (code == Loc.Language)
            return;
        _config.Language = code;
        Loc.Apply(code);
        if (!TrySave(out var error))
            LoadError = error;
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            _searchText = value;
            View.Refresh();
            OnPropertyChanged();
            RaiseListState();
        }
    }

    public bool Paused
    {
        get => _engine.Paused;
        set
        {
            if (_engine.Paused == value)
                return;
            _engine.Paused = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(PauseLabel));
            PausedChanged?.Invoke(value);
        }
    }

    public string StatusText => Loc.T(Paused ? "Status.Paused" : "Status.Listening");
    public string PauseLabel => Loc.T(Paused ? "Status.Resume" : "Status.Pause");

    public ListScope Scope => _scope;
    public bool IsProfileScope => _scope == ListScope.Profile;

    public string PageTitle => _scope switch
    {
        ListScope.Macros => Loc.T("Nav.Macros"),
        ListScope.Profile => _profileName,
        _ => Loc.T("Nav.Bindings"),
    };

    public string Summary
    {
        get
        {
            var inScope = Items.Where(InScope).ToList();
            int active = inScope.Count(i => i.Enabled && !i.HasError);
            return _scope switch
            {
                ListScope.Macros => Loc.F(inScope.Count == 1 ? "List.Summary.Macro1" : "List.Summary.MacroN", inScope.Count, active),
                ListScope.Profile when _profileKey is null => Loc.F("List.Summary.Global", active),
                ListScope.Profile => Loc.F("List.Summary.App", active, _profileName),
                _ => Loc.F("List.Summary.All", active, Profiles.Count),
            };
        }
    }

    public bool IsEmpty => View.IsEmpty;

    public string EmptyText
    {
        get
        {
            if (Items.Count == 0)
                return Loc.T("List.Empty.First");
            if (!string.IsNullOrWhiteSpace(_searchText))
                return Loc.T("List.Empty.Search");
            return Loc.T(_scope == ListScope.Macros ? "List.Empty.Macros" : "List.Empty.Profile");
        }
    }

    public string? LoadError
    {
        get => _loadError;
        private set
        {
            _loadError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLoadError));
        }
    }

    public bool HasLoadError => LoadError is not null;

    /// <summary>Reloads config.json. On a bad file the previous bindings stay active.</summary>
    public void Reload()
    {
        try
        {
            _config = _store.Load();
            LoadError = null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LoadError = Loc.F("List.LoadError", ex.Message);
            return;
        }

        // The app's own starter bindings are safe, and so is an existing AppData config the first time this
        // check runs (an upgrade). A portable config.json has to be approved, since anyone may have put it there.
        if (_store.CreatedStarter || (_trust.IsNew && !ConfigStore.IsPortable))
            _trust.Trust(_config.Bindings);

        // Switching language rebuilds the list itself (subtitles and keycaps are translated).
        if (Loc.Resolve(_config.Language) != Loc.Language)
            Loc.Apply(_config.Language);
        else
            RebuildItems();
    }

    private void OnLanguageChanged()
    {
        if (_scope == ListScope.Profile && _profileKey is null)
            _profileName = Loc.T("Profiles.Global");
        RebuildItems();
        OnPropertyChanged(string.Empty); // Every text property may have changed.
    }

    public void ShowAll() => SetScope(ListScope.All, null, "");

    public void ShowMacros() => SetScope(ListScope.Macros, null, "");

    public void ShowProfile(ProfileSummary profile) => SetScope(ListScope.Profile, profile.AppKey, profile.Name);

    /// <summary>Starting point for a new binding that fits the list being shown.</summary>
    public HotkeyBinding NewDraft() => new()
    {
        App = _scope == ListScope.Profile && _profileKey is not null ? _profileName : null,
        Action = _scope == ListScope.Macros ? new ActionSpec { Type = "macro", Steps = new() } : new ActionSpec(),
    };

    /// <summary>Name of another enabled binding that reacts to the same keys in the same app, if any.</summary>
    public string? FindConflict(TriggerSpec trigger, string? app, HotkeyBinding? except)
    {
        string? appKey = string.IsNullOrWhiteSpace(app) ? null : ForegroundTracker.Normalize(app);
        return Items.FirstOrDefault(i => i.Model != except && i.Enabled && i.IsValid
            && i.Trigger!.Id == trigger.Id && i.AppKey == appKey)?.Name;
    }

    /// <summary>Adds <paramref name="updated"/>, or puts it in place of <paramref name="original"/>.</summary>
    public bool SaveBinding(HotkeyBinding? original, HotkeyBinding updated, out string? error)
    {
        int index = original is null ? -1 : _config.Bindings.IndexOf(original);
        if (index >= 0)
            _config.Bindings[index] = updated;
        else
            _config.Bindings.Add(updated);
        _trust.Trust(new[] { updated }); // Made in the editor, so the user has seen it.
        RebuildItems();
        return TrySave(out error);
    }

    public bool DeleteBinding(HotkeyBinding binding, out string? error)
    {
        _config.Bindings.Remove(binding);
        RebuildItems();
        return TrySave(out error);
    }

    private void SetScope(ListScope scope, string? profileKey, string profileName)
    {
        _scope = scope;
        _profileKey = profileKey;
        _profileName = profileName;
        View.Refresh();
        OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(IsProfileScope));
        OnPropertyChanged(nameof(PageTitle));
        RaiseListState();
    }

    private void RebuildItems()
    {
        Items.Clear();
        foreach (var binding in _config.Bindings)
            Items.Add(new BindingItem(binding, OnItemToggled, _trust.IsTrusted(binding)));
        RebuildProfiles();
        ApplyBindings();
    }

    private void RebuildProfiles()
    {
        Profiles.Clear();
        Profiles.Add(new ProfileSummary(null, Loc.T("Profiles.Global"), Items.Count(i => i.AppKey is null)));
        foreach (var group in Items.Where(i => i.AppKey is not null).GroupBy(i => i.AppKey).OrderBy(g => g.Key))
            Profiles.Add(new ProfileSummary(group.Key, group.First().Model.App!.Trim(), group.Count()));
    }

    private void OnItemToggled(BindingItem item)
    {
        ApplyBindings();
        if (!TrySave(out var error))
            LoadError = error;
    }

    private bool TrySave(out string? error)
    {
        try
        {
            _store.Save(_config);
            _trust.Prune(_config.Bindings);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = Loc.F("List.SaveError", ex.Message);
            return false;
        }
    }

    /// <summary>Flags duplicate triggers and hands the usable bindings to the engine.</summary>
    private void ApplyBindings()
    {
        var active = new Dictionary<(string, string?), BindingItem>();
        foreach (var item in Items)
        {
            item.Conflict = null;
            if (!item.Enabled || !item.IsValid || !item.IsTrusted)
                continue;
            var key = (item.Trigger!.Id, item.AppKey);
            if (active.TryGetValue(key, out var first))
                item.Conflict = Loc.F("List.Conflict", first.Name);
            else
                active[key] = item;
        }

        _engine.SequenceTimeoutMs = Math.Clamp(_config.SequenceTimeoutMs ?? HotkeyEngine.DefaultSequenceTimeoutMs,
            HotkeyEngine.MinSequenceTimeoutMs, HotkeyEngine.MaxSequenceTimeoutMs);
        _engine.SetBindings(active.Values.Select(item => (item.Trigger!, item.Model)));
        RaiseListState();
    }

    private void RaiseListState()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(HasUntrusted));
        OnPropertyChanged(nameof(TrustBanner));
    }

    private bool InScope(BindingItem item) => _scope switch
    {
        ListScope.Macros => item.IsMacro,
        ListScope.Profile => item.AppKey == _profileKey,
        _ => true,
    };

    private bool Matches(object obj)
    {
        var item = (BindingItem)obj;
        if (!InScope(item))
            return false;
        if (string.IsNullOrWhiteSpace(_searchText))
            return true;
        var query = _searchText.Trim();
        return item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Model.Trigger.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
