using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace MacroInWZa007.Localization;

/// <summary>
/// UI language switching. Code reads strings with <see cref="T"/> and <see cref="F"/>;
/// XAML reads the same keys with {DynamicResource Key}, so a switch updates open windows in place.
/// </summary>
public static class Loc
{
    public static readonly IReadOnlyList<(string Code, string NativeName)> Languages = new[]
    {
        ("en", "English"),
        ("th", "ไทย"),
        ("ja", "日本語"),
        ("ko", "한국어"),
        ("zh", "中文"),
        ("ru", "Русский"),
    };

    private static IReadOnlyDictionary<string, string> _strings = Strings.En;
    private static ResourceDictionary? _resources;

    /// <summary>Raised after the language changes, for text built in code.</summary>
    public static event Action? Changed;

    public static string Language { get; private set; } = "en";

    public static string T(string key) =>
        _strings.TryGetValue(key, out var text) || Strings.En.TryGetValue(key, out text) ? text : key;

    public static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// <summary>A supported language code: the one asked for, else the Windows display language, else English.</summary>
    public static string Resolve(string? code)
    {
        foreach (var candidate in new[] { code, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName })
        {
            var normalized = (candidate ?? "").Trim().ToLowerInvariant();
            if (Languages.Any(l => l.Code == normalized))
                return normalized;
        }
        return "en";
    }

    /// <summary>Switches the UI language. Call on the UI thread.</summary>
    public static void Apply(string? code)
    {
        Language = Resolve(code);
        _strings = Language switch
        {
            "th" => Strings.Th,
            "ja" => Strings.Ja,
            "ko" => Strings.Ko,
            "zh" => Strings.Zh,
            "ru" => Strings.Ru,
            _ => Strings.En,
        };

        var resources = new ResourceDictionary();
        foreach (var key in Strings.En.Keys)
            resources[key] = T(key);
        // Bundled fonts need the base URI passed separately; a pack URI inside the name is silently ignored.
        resources["UiFont"] = new FontFamily(new Uri("pack://application:,,,/"), FontFor(Language));

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_resources is not null)
            merged.Remove(_resources);
        merged.Add(resources); // Added last, so it overrides the theme's UiFont.
        _resources = resources;

        Changed?.Invoke();
    }

    private const string Bundled = "./Assets/Fonts/#";

    // Inter (bundled) covers Latin and Cyrillic. Thai uses the bundled IBM Plex Sans Thai Looped, whose
    // looped letterforms stay easy to tell apart in long use. CJK falls to a Windows font for the script,
    // so Han characters get the right regional shapes instead of whatever WPF picks.
    private static string FontFor(string language) => language switch
    {
        "th" => $"{Bundled}Inter, {Bundled}IBM Plex Sans Thai Looped, Leelawadee UI",
        "ja" => $"{Bundled}Inter, Yu Gothic UI, Meiryo UI, Segoe UI",
        "ko" => $"{Bundled}Inter, Malgun Gothic, Segoe UI",
        "zh" => $"{Bundled}Inter, Microsoft YaHei UI, Segoe UI",
        _ => $"{Bundled}Inter, {Bundled}IBM Plex Sans Thai Looped, Segoe UI",
    };
}
