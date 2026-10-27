using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Clayboard.Core;

namespace Clayboard.Config;

/// <summary>
/// Loads and saves config.json and reports edits made to it from outside the app.
/// The file lives next to the exe when one is already there (portable mode),
/// otherwise in %AppData%\Clayboard.
/// </summary>
public sealed class ConfigStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        // Writes "+" as-is instead of as a unicode escape, so "Ctrl+Alt+T" stays readable when hand-edited.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private long _lastSaveTicks;

    public ConfigStore()
    {
        var folder = IsPortable ? AppContext.BaseDirectory : AppDataFolder;
        FilePath = Path.Combine(folder, "config.json");

        _debounce = new Timer(_ => OnDebounced());
        _watcher = new FileSystemWatcher(folder, "config.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += (_, _) => _debounce.Change(300, Timeout.Infinite);
        _watcher.Created += (_, _) => _debounce.Change(300, Timeout.Infinite);
        _watcher.Renamed += (_, _) => _debounce.Change(300, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
    }

    public string FilePath { get; }

    /// <summary>%AppData%\Clayboard, filled from the app's former folder the first time it's used.</summary>
    public static string AppDataFolder { get; } = PrepareAppDataFolder();

    private static string PrepareAppDataFolder()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(roaming, "Clayboard");
        var legacy = Path.Combine(roaming, "MacroInWZa007"); // The app's name before it became Clayboard.
        if (!Directory.Exists(folder) && Directory.Exists(legacy))
        {
            // Copy rather than move, so the old folder is still there if anything goes wrong.
            Directory.CreateDirectory(folder);
            foreach (var file in Directory.GetFiles(legacy, "*.json"))
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        }
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>True when a config.json sits next to the exe, so settings travel with the folder.</summary>
    public static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, "config.json"));

    /// <summary>Raised on a worker thread after config.json is edited outside the app.</summary>
    public event Action? Changed;

    /// <summary>True when the last <see cref="Load"/> wrote the built-in starter bindings.</summary>
    public bool CreatedStarter { get; private set; }

    /// <summary>JSON exactly as config.json writes it, for fingerprinting actions.</summary>
    public static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>Reads the config, creating a starter file on first run.</summary>
    public AppConfig Load()
    {
        // An empty file is how a fresh portable copy asks for the starter bindings.
        CreatedStarter = !File.Exists(FilePath) || new FileInfo(FilePath).Length == 0;
        if (CreatedStarter)
        {
            var starter = CreateStarter();
            Save(starter);
            return starter;
        }

        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOptions) ?? new AppConfig();
        config.Bindings ??= new();
        foreach (var binding in config.Bindings)
            binding.Action ??= new ActionSpec();
        return config;
    }

    public void Save(AppConfig config)
    {
        Interlocked.Exchange(ref _lastSaveTicks, DateTime.UtcNow.Ticks);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }

    private void OnDebounced()
    {
        // Our own saves also trip the watcher; only report edits made by someone else.
        var sinceSave = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastSaveTicks));
        if (sinceSave > TimeSpan.FromSeconds(1.5))
            Changed?.Invoke();
    }

    private static AppConfig CreateStarter() => new()
    {
        Bindings =
        {
            new() { Name = "Open terminal", Trigger = "Ctrl+Alt+T", Action = new() { Type = "run", Path = "wt.exe" } },
            new() { Name = "Open Notepad", Trigger = "Ctrl+Alt+N", Action = new() { Type = "run", Path = "notepad.exe" } },
            new() { Name = "Type my email", Trigger = "Ctrl+Alt+E", Action = new() { Type = "text", Value = "you@example.com" } },
            new() { Name = "Play / pause", Trigger = "Ctrl+Alt+Space", Action = new() { Type = "media", Command = "playPause" } },
            new() { Name = "Volume up", Trigger = "Ctrl+Alt+PageUp", Action = new() { Type = "media", Command = "volumeUp" } },
            new() { Name = "Volume down", Trigger = "Ctrl+Alt+PageDown", Action = new() { Type = "media", Command = "volumeDown" } },
            new() { Name = "Escape", Trigger = "CapsLock", Enabled = false, Action = new() { Type = "sendKeys", Keys = "Esc" } },
        },
    };
}
