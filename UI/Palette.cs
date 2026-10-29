using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Clayboard.UI;

/// <summary>One color theme: the eleven colors Theme.xaml and the windows build everything from.</summary>
public sealed record ThemeColors(
    string Key, string Name, bool IsDark,
    Color Bg, Color Card, Color Line, Color Sand, Color Ink, Color Mute,
    Color Clay, Color Sage, Color Terra, Color TerraSoft, Color OnClay);

/// <summary>
/// The app's color themes. "original" (the earth-tone palette, always light) is the default;
/// "system" follows Windows (original in light mode, espresso in dark, the system colors in high contrast);
/// the rest are fixed themes picked in Settings. Theme.xaml and the windows refer to the colors by key.
/// </summary>
public static class Palette
{
    public const string OriginalKey = "original";
    public const string SystemKey = "system";

    private static ResourceDictionary? _colors;
    private static ResourceDictionary? _theme;

    /// <summary>The theme picked in Settings (a key from <see cref="Themes"/>, or <see cref="SystemKey"/>).</summary>
    public static string CurrentKey { get; private set; } = OriginalKey;

    /// <summary>The colors in use right now; for "system", whichever ones Windows asked for.</summary>
    public static ThemeColors CurrentColors { get; private set; } = null!;

    /// <summary>Every fixed theme, original first. The names are the same in every language.</summary>
    public static IReadOnlyList<ThemeColors> Themes { get; } = new[]
    {
        T(OriginalKey, "Original", false, "#F5EFE6", "#FBF8F3", "#E6DCCB", "#EDE4D6", "#3B2F26", "#8A7A69", "#8B5E3C", "#7D8B6A", "#B5653F", "#F3E1D6", "#FBF8F3"),
        // Light
        T("sakura", "Sakura", false, "#F8EEF0", "#FFF8F9", "#EED5DA", "#F3E2E6", "#3D2B30", "#8E7178", "#B8506E", "#7E9B78", "#C0504D", "#F6DCDC", "#FFF8F9"),
        T("matcha", "Matcha", false, "#EEF2E6", "#F8FAF3", "#D8E0C8", "#E4EAD8", "#2E3626", "#717C66", "#557A3D", "#6F9A5B", "#B5653F", "#F1E0D4", "#F8FAF3"),
        T("ocean", "Ocean", false, "#EAF1F6", "#F7FAFC", "#D3E0EA", "#DFE9F1", "#22313D", "#6B7E8C", "#2F6E9E", "#5E9A7E", "#C25B4E", "#F5DEDA", "#F7FAFC"),
        T("lavender", "Lavender", false, "#F1EEF7", "#FAF8FD", "#DDD6EA", "#E8E3F1", "#302A3D", "#7A7190", "#6A51A0", "#6F9A82", "#C0555F", "#F5DDE0", "#FAF8FD"),
        T("dune", "Dune", false, "#F4EEDF", "#FBF8EE", "#E5DBC2", "#ECE4CF", "#3A3222", "#857A62", "#9C6F22", "#7D8B5A", "#B5553F", "#F3E0D4", "#FBF8EE"),
        T("paper", "Paper", false, "#F3F3F1", "#FCFCFB", "#E2E2DE", "#EAEAE6", "#2B2B2A", "#7A7A76", "#3F3F3D", "#6E8B6A", "#B5503F", "#F3DFDA", "#FCFCFB"),
        T("peach", "Peach", false, "#FBEFE8", "#FFF9F5", "#F1D9CC", "#F6E4DA", "#3E2C24", "#8C7266", "#C4623A", "#7E9A70", "#C04B3E", "#F7DCD5", "#FFF9F5"),
        T("mint", "Mint", false, "#EBF5F2", "#F7FCFA", "#D2E6E0", "#DEEEE9", "#213430", "#69807A", "#277A68", "#4F9C6B", "#C2584A", "#F5DEDA", "#F7FCFA"),
        T("lemon", "Lemon", false, "#FAF6E3", "#FFFDF3", "#EDE5BF", "#F3EDD2", "#3A3520", "#857E5C", "#D9B33C", "#7A9150", "#B5553F", "#F3E0D4", "#2A2510"),
        T("rosegold", "Rose Gold", false, "#F6EDEA", "#FDF8F6", "#E9D7D1", "#EFE2DD", "#3B2C29", "#88726C", "#A65E69", "#7E9578", "#B5503F", "#F4DCD8", "#FDF8F6"),
        // Dark
        T("espresso", "Espresso", true, "#1E1915", "#29231E", "#3E352C", "#332B24", "#EEE6DA", "#A69684", "#C48D61", "#97A882", "#E0916A", "#3B2820", "#1E1915"),
        T("midnight", "Midnight", true, "#15181E", "#1D2129", "#2D333D", "#252A33", "#E3E7EE", "#8C95A3", "#6B98F0", "#5FB88A", "#E0715F", "#3A2422", "#15181E"),
        T("forest", "Forest", true, "#151C17", "#1C251F", "#2B372E", "#233027", "#E2EBE3", "#8DA190", "#86B86F", "#7FB069", "#E07B5F", "#3A2620", "#151C17"),
        T("plum", "Plum", true, "#1D1622", "#271F2D", "#3A2F42", "#302738", "#ECE3F1", "#9C8DA8", "#B88AE0", "#7FB08F", "#E57A86", "#3E2228", "#1D1622"),
        T("deepsea", "Deep Sea", true, "#0F1A22", "#16232D", "#24363F", "#1D2C36", "#DDE9F0", "#819AA8", "#45B0D3", "#5BB59A", "#E37C6B", "#382421", "#0F1A22"),
        T("charcoal", "Charcoal", true, "#1A1A1A", "#232323", "#343434", "#2B2B2B", "#E8E8E8", "#959595", "#D0D0D0", "#7FB07F", "#E07A6A", "#3A2422", "#1A1A1A"),
        T("ember", "Ember", true, "#1F1512", "#2A1D18", "#3E2B23", "#33241E", "#F0E2DA", "#A8907F", "#EC7D48", "#9AAE7A", "#F0675A", "#3F221D", "#1F1512"),
        T("aurora", "Aurora", true, "#101C1C", "#172626", "#253838", "#1E2F2F", "#DDF0EC", "#86A6A1", "#4FD1A5", "#8FC46A", "#EB7A7A", "#3A2324", "#101C1C"),
        T("neon", "Neon", true, "#14111F", "#1C182B", "#2E2842", "#251F37", "#EAE6F7", "#9590B0", "#FF6AA8", "#5FD3A0", "#FF6B6B", "#3D1F2B", "#14111F"),
        T("slate", "Slate", true, "#1B1F2A", "#232836", "#343B4D", "#2B3142", "#E1E5F0", "#8F97AD", "#8EA8FF", "#7CC49A", "#E8806E", "#3B2524", "#1B1F2A"),
    };

    private static ThemeColors Espresso => Themes.First(t => t.Key == "espresso");

    /// <summary>A theme key from config.json made safe: unknown or missing means the original.</summary>
    public static string Normalize(string? key) =>
        key == SystemKey || Themes.Any(t => t.Key == key) ? key! : OriginalKey;

    /// <summary>What the "system" theme shows right now, from Windows' light, dark, and high contrast settings.</summary>
    public static ThemeColors FromWindows()
    {
        if (SystemParameters.HighContrast)
            return HighContrast();
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is 0 ? Espresso : Themes[0];
    }

    /// <summary>The colors <paramref name="key"/> stands for at the moment.</summary>
    public static ThemeColors Resolve(string key) =>
        key == SystemKey ? FromWindows() : Themes.FirstOrDefault(t => t.Key == key) ?? Themes[0];

    /// <summary>Raised after <see cref="Apply"/> switches colors, for UI that draws theme previews.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Switches every open window to the theme right away. The XAML refers to the brushes with DynamicResource,
    /// so swapping them in the app's resources repaints everything in place, the way switching language does.
    /// Returns false when nothing changed.
    /// </summary>
    public static bool Apply(string? key)
    {
        key = Normalize(key);
        var colors = Resolve(key);
        if (_colors is not null && key == CurrentKey && colors == CurrentColors)
            return false;

        CurrentKey = key;
        CurrentColors = colors;
        if (_colors is null)
        {
            // First time: the brushes go in ahead of Theme.xaml, whose styles look them up.
            _colors = new ResourceDictionary();
            Fill(_colors, colors);
            var merged = Application.Current.Resources.MergedDictionaries;
            merged.Insert(0, _colors);
            _theme = new ResourceDictionary { Source = new Uri("pack://application:,,,/UI/Theme.xaml") };
            merged.Insert(1, _theme);
        }
        else
        {
            Fill(_colors, colors);
        }
        Changed?.Invoke();
        return true;
    }

    private static ThemeColors HighContrast() => new(SystemKey, "High contrast", true,
        SystemColors.WindowColor, SystemColors.WindowColor, SystemColors.WindowTextColor, SystemColors.HighlightColor,
        SystemColors.WindowTextColor, SystemColors.GrayTextColor, SystemColors.HighlightColor, SystemColors.HighlightColor,
        SystemColors.HotTrackColor, SystemColors.WindowColor, SystemColors.HighlightTextColor);

    private static void Fill(ResourceDictionary colors, ThemeColors t)
    {
        foreach (var (key, color) in new[]
                 {
                     ("BgBrush", t.Bg), ("CardBrush", t.Card), ("LineBrush", t.Line), ("SandBrush", t.Sand),
                     ("InkBrush", t.Ink), ("MuteBrush", t.Mute), ("ClayBrush", t.Clay), ("SageBrush", t.Sage),
                     ("TerraBrush", t.Terra), ("TerraSoftBrush", t.TerraSoft), ("OnClayBrush", t.OnClay),
                 })
        {
            colors[key] = Frozen(color);
        }
    }

    /// <summary>A frozen brush, cheap to share between controls (theme swatches in Settings use these too).</summary>
    public static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static ThemeColors T(string key, string name, bool isDark, params string[] hex)
    {
        var c = hex.Select(h => (Color)ColorConverter.ConvertFromString(h)).ToArray();
        return new ThemeColors(key, name, isDark, c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c[10]);
    }
}
