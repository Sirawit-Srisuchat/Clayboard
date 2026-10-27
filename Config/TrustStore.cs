using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clayboard.Core;

namespace Clayboard.Config;

/// <summary>
/// Remembers which actions the user has seen and approved, as fingerprints, so an action that
/// appears in config.json from outside the app (an edited or planted file) stays off until
/// the user allows it. Lives in %AppData% even in portable mode, so a config.json carried in
/// next to the exe can't vouch for itself.
/// </summary>
public sealed class TrustStore
{
    private readonly string _path;
    private readonly HashSet<string> _trusted;

    public TrustStore()
    {
        _path = Path.Combine(ConfigStore.AppDataFolder, "trusted.json");
        IsNew = !File.Exists(_path);
        _trusted = IsNew ? new HashSet<string>() : Read(_path);
    }

    /// <summary>True when no approvals were ever saved: the first run, or an upgrade from before this check.</summary>
    public bool IsNew { get; private set; }

    public bool IsTrusted(HotkeyBinding binding) => _trusted.Contains(Fingerprint(binding));

    /// <summary>Approves the bindings' actions.</summary>
    public void Trust(IEnumerable<HotkeyBinding> bindings)
    {
        foreach (var binding in bindings)
            _trusted.Add(Fingerprint(binding));
        Save();
    }

    /// <summary>Forgets approvals for actions no longer in the config, without approving anything new.</summary>
    public void Prune(IEnumerable<HotkeyBinding> current)
    {
        var keep = current.Select(Fingerprint).ToHashSet();
        if (_trusted.RemoveWhere(f => !keep.Contains(f)) > 0)
            Save();
    }

    /// <summary>A hash of what the binding does; trigger, name, and on/off changes don't need approval.</summary>
    private static string Fingerprint(HotkeyBinding binding) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ConfigStore.Serialize(binding.Action))));

    private void Save()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_trusted.OrderBy(f => f)));
        File.Move(temp, _path, overwrite: true);
        IsNew = false;
    }

    private static HashSet<string> Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path)) ?? new HashSet<string>();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new HashSet<string>(); // Unreadable: nothing is trusted, so the user re-approves.
        }
    }
}
