using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Clayboard.Core;
using Clayboard.Localization;

namespace Clayboard.UI;

/// <summary>The bindings list: groups, the user's own order, sorting, filtering, and moving rows.</summary>
public sealed partial class MainViewModel
{
    public static readonly IReadOnlyList<string> SortModes = new[] { "manual", "name", "nameDesc", "keys", "type" };
    public static readonly IReadOnlyList<string> ShowModes = new[] { "all", "on", "off", "problems" };

    // One header per group, reused across rebuilds so the list only moves rows instead of recreating them.
    private readonly Dictionary<string, GroupHeader> _headers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the list shows, top to bottom: <see cref="GroupHeader"/>s and <see cref="BindingItem"/>s.</summary>
    public ObservableCollection<object> Rows { get; } = new();

    public IReadOnlyList<string> Groups => _config.Groups;

    public string SortMode => _config.Sort is { } sort && SortModes.Contains(sort) ? sort : "manual";
    public string ShowMode => _config.Show is { } show && ShowModes.Contains(show) ? show : "all";

    /// <summary>Rows can be dragged or moved only while the list shows everything in the user's own order.</summary>
    public bool CanReorder => SortMode == "manual" && ShowMode == "all" && string.IsNullOrWhiteSpace(_searchText);

    public bool ShowReorderHint => !CanReorder;

    public string SortLabel => $"{Loc.T("List.Sort")}: {SortName(SortMode)}";
    public string ShowLabel => $"{Loc.T("List.Show")}: {ShowName(ShowMode)}";

    public static string SortName(string mode) => Loc.T(mode switch
    {
        "name" => "Sort.Name",
        "nameDesc" => "Sort.NameDesc",
        "keys" => "Sort.Keys",
        "type" => "Sort.Type",
        _ => "Sort.Manual",
    });

    public static string ShowName(string mode) => Loc.T(mode switch
    {
        "on" => "Show.On",
        "off" => "Show.Off",
        "problems" => "Show.Problems",
        _ => "Show.All",
    });

    public void SetSort(string mode)
    {
        _config.Sort = mode == "manual" ? null : mode;
        SaveOrReport();
        RebuildRows();
    }

    public void SetShow(string mode)
    {
        _config.Show = mode == "all" ? null : mode;
        SaveOrReport();
        RebuildRows();
    }

    public void ToggleCollapsed(GroupHeader header)
    {
        var key = KeyOf(header.Name);
        if (!_config.CollapsedGroups.Remove(key))
            _config.CollapsedGroups.Add(key);
        SaveOrReport();
        RebuildRows();
    }

    /// <summary>Creates an empty group at the end. Returns an error to show, or null.</summary>
    public string? AddGroup(string name)
    {
        name = name.Trim();
        if (GroupNameError(name, except: null) is { } error)
            return error;
        _config.Groups.Add(name);
        SaveOrReport();
        RebuildRows();
        return null;
    }

    public string? RenameGroup(string oldName, string newName)
    {
        newName = newName.Trim();
        if (GroupNameError(newName, except: oldName) is { } error)
            return error;
        int index = _config.Groups.FindIndex(g => Same(g, oldName));
        if (index < 0)
            return null;
        _config.Groups[index] = newName;
        foreach (var binding in _config.Bindings.Where(b => Same(b.Group, oldName)))
            binding.Group = newName;
        if (_config.CollapsedGroups.RemoveAll(g => Same(g, oldName)) > 0)
            _config.CollapsedGroups.Add(newName);
        _headers.Remove(oldName);
        SaveOrReport();
        RebuildRows();
        return null;
    }

    /// <summary>Removes the group; its bindings stay, moved to "no group".</summary>
    public void DeleteGroup(string name)
    {
        _config.Groups.RemoveAll(g => Same(g, name));
        _config.CollapsedGroups.RemoveAll(g => Same(g, name));
        foreach (var binding in _config.Bindings.Where(b => Same(b.Group, name)))
            binding.Group = null;
        _headers.Remove(name);
        SaveOrReport();
        RebuildRows();
    }

    private string? GroupNameError(string name, string? except)
    {
        if (name.Length == 0)
            return Loc.T("Group.Empty");
        if (_config.Groups.Any(g => Same(g, name) && !Same(g, except)))
            return Loc.F("Group.Exists", name);
        return null;
    }

    /// <summary>
    /// Drops a dragged row next to <paramref name="target"/>. A binding lands in the target's group;
    /// a group header reorders the groups.
    /// </summary>
    public void MoveRow(object dragged, object target, bool after)
    {
        if (!CanReorder || ReferenceEquals(dragged, target))
            return;
        switch (dragged, target)
        {
            case (BindingItem item, BindingItem onto):
                MoveBinding(item, onto.Model.Group, after ? NextInGroup(onto.Model) : onto.Model);
                break;
            case (BindingItem item, GroupHeader header):
                if (after || PreviousSection(header.Name) is not { } previous)
                    MoveBinding(item, header.Name, FirstInGroup(header.Name)); // Top of this group.
                else
                    MoveBinding(item, previous.Name, null); // Above a header: end of the group before it.
                break;
            case (GroupHeader moving, _) when moving.IsNamed:
                var targetGroup = target switch
                {
                    GroupHeader header => header.Name,
                    BindingItem onto => onto.Model.Group,
                    _ => null,
                };
                MoveGroup(moving.Name!, targetGroup, after || target is BindingItem);
                break;
        }
    }

    /// <summary>Moves a binding one place up the list, into the end of the group above when it's first in its own.</summary>
    public void MoveUp(BindingItem item)
    {
        var ordered = OrderedBindings();
        int index = ordered.IndexOf(item.Model);
        if (index <= 0)
        {
            // First in its section: hop into the end of the previous section, if there is one.
            if (PreviousSection(item.Model.Group) is { } previous)
                MoveBinding(item, previous.Name, null);
            return;
        }
        var above = ordered[index - 1];
        if (Same(above.Group, item.Model.Group))
            MoveBinding(item, item.Model.Group, above);
        else
            MoveBinding(item, above.Group, null);
    }

    public void MoveDown(BindingItem item)
    {
        var ordered = OrderedBindings();
        int index = ordered.IndexOf(item.Model);
        if (index < 0)
            return;
        if (index == ordered.Count - 1)
        {
            if (NextSection(item.Model.Group) is { } next)
                MoveBinding(item, next.Name, FirstInGroup(next.Name));
            return;
        }
        var below = ordered[index + 1];
        if (Same(below.Group, item.Model.Group))
            MoveBinding(item, item.Model.Group, NextInGroup(below));
        else
            MoveBinding(item, below.Group, below);
    }

    private void MoveBinding(BindingItem item, string? group, HotkeyBinding? before)
    {
        if (ReferenceEquals(before, item.Model))
            return;
        _config.Bindings.Remove(item.Model);
        item.Model.Group = group;
        int index = before is null ? IndexAfterGroup(group) : _config.Bindings.IndexOf(before);
        _config.Bindings.Insert(index < 0 ? _config.Bindings.Count : index, item.Model);
        SaveOrReport();
        RebuildRows();
    }

    private void MoveGroup(string name, string? targetGroup, bool after)
    {
        _config.Groups.RemoveAll(g => Same(g, name));
        int index = targetGroup is null ? 0 : _config.Groups.FindIndex(g => Same(g, targetGroup));
        if (index < 0)
            index = _config.Groups.Count;
        else if (after && targetGroup is not null)
            index++;
        _config.Groups.Insert(index, name);
        SaveOrReport();
        RebuildRows();
    }

    /// <summary>Bindings in display order when the list is unsorted: no-group first, then each group in turn.</summary>
    private List<HotkeyBinding> OrderedBindings() =>
        Sections().SelectMany(section => _config.Bindings.Where(b => Same(b.Group, section))).ToList();

    private IEnumerable<string?> Sections() => new string?[] { null }.Concat(_config.Groups);

    private HotkeyBinding? FirstInGroup(string? group) => _config.Bindings.FirstOrDefault(b => Same(b.Group, group));

    private HotkeyBinding? NextInGroup(HotkeyBinding binding)
    {
        int index = _config.Bindings.IndexOf(binding);
        return _config.Bindings.Skip(index + 1).FirstOrDefault(b => Same(b.Group, binding.Group));
    }

    private int IndexAfterGroup(string? group)
    {
        int last = _config.Bindings.FindLastIndex(b => Same(b.Group, group));
        return last < 0 ? _config.Bindings.Count : last + 1;
    }

    private GroupHeader? PreviousSection(string? group)
    {
        var sections = Sections().ToList();
        int index = sections.FindIndex(s => Same(s, group));
        return index > 0 ? HeaderFor(sections[index - 1]) : null;
    }

    private GroupHeader? NextSection(string? group)
    {
        var sections = Sections().ToList();
        int index = sections.FindIndex(s => Same(s, group));
        return index >= 0 && index < sections.Count - 1 ? HeaderFor(sections[index + 1]) : null;
    }

    /// <summary>
    /// Tidies group data from config.json: trims names, drops blanks and duplicates, makes each binding
    /// use its group's exact spelling, and adds groups that bindings mention but the list doesn't.
    /// </summary>
    private void NormalizeGroups()
    {
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<string>();
        foreach (var name in _config.Groups.Select(g => g?.Trim()).Where(g => !string.IsNullOrEmpty(g)))
        {
            if (canonical.TryAdd(name!, name!))
                groups.Add(name!);
        }
        foreach (var binding in _config.Bindings)
        {
            var name = binding.Group?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                binding.Group = null;
                continue;
            }
            if (!canonical.TryGetValue(name, out var spelled))
            {
                canonical[name] = spelled = name;
                groups.Add(name);
            }
            binding.Group = spelled;
        }
        _config.Groups = groups;
        _config.CollapsedGroups = _config.CollapsedGroups
            .Where(g => g == "" || canonical.ContainsKey(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Rebuilds the visible rows, then updates <see cref="Rows"/> with moves and inserts only.</summary>
    private void RebuildRows()
    {
        var itemOf = Items.ToDictionary(i => i.Model);
        bool hasGroups = _config.Groups.Count > 0;
        bool filtering = !string.IsNullOrWhiteSpace(_searchText) || ShowMode != "all" || _scope != ListScope.All;
        var rows = new List<object>();

        foreach (var section in Sections())
        {
            var items = Sort(_config.Bindings
                .Where(b => Same(b.Group, section) && itemOf.ContainsKey(b))
                .Select(b => itemOf[b])
                .Where(Matches)).ToList();

            var header = HeaderFor(section);
            header.Count = items.Count;
            header.IsCollapsed = _config.CollapsedGroups.Contains(KeyOf(section), StringComparer.OrdinalIgnoreCase);
            header.RefreshText();

            // Named groups show even when empty, so there's somewhere to drop bindings into; while filtering,
            // only groups with matches show. "No group" gets a header only when other groups exist.
            bool mustHaveItems = section is null || filtering;
            bool showHeader = hasGroups && (!mustHaveItems || items.Count > 0);
            if (showHeader)
                rows.Add(header);
            if (!showHeader || !header.IsCollapsed)
                rows.AddRange(items);
        }

        Sync(Rows, rows);
        RaiseListState();
    }

    private IEnumerable<BindingItem> Sort(IEnumerable<BindingItem> items)
    {
        var byName = StringComparer.CurrentCultureIgnoreCase;
        return SortMode switch
        {
            "name" => items.OrderBy(i => i.Name, byName),
            "nameDesc" => items.OrderByDescending(i => i.Name, byName),
            "keys" => items.OrderBy(i => i.Model.Trigger, byName).ThenBy(i => i.Name, byName),
            "type" => items.OrderBy(i => i.Model.Action.Type, byName).ThenBy(i => i.Name, byName),
            _ => items,
        };
    }

    // Moves rows that already exist instead of removing and re-adding them, so their controls (and the
    // fade-in animation) are reused and only genuinely new rows animate in.
    private static void Sync(ObservableCollection<object> current, IReadOnlyList<object> wanted)
    {
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < current.Count && ReferenceEquals(current[i], wanted[i]))
                continue;
            int existing = -1;
            for (int j = i + 1; j < current.Count; j++)
            {
                if (ReferenceEquals(current[j], wanted[i]))
                {
                    existing = j;
                    break;
                }
            }
            if (existing >= 0)
                current.Move(existing, i);
            else
                current.Insert(i, wanted[i]);
        }
        while (current.Count > wanted.Count)
            current.RemoveAt(current.Count - 1);
    }

    private GroupHeader HeaderFor(string? group)
    {
        var key = KeyOf(group);
        if (!_headers.TryGetValue(key, out var header))
            _headers[key] = header = new GroupHeader(group);
        return header;
    }

    // "" stands for "no group" wherever a name is needed as a key.
    private static string KeyOf(string? group) => group ?? "";

    private static bool Same(string? a, string? b) =>
        string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
}
