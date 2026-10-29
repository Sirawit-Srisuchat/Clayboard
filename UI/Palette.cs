using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Clayboard.UI;

/// <summary>
/// The app's colors, picked to match Windows: the earth-tone palette in light or dark, or the
/// system colors under high contrast. Theme.xaml and the windows refer to these by key.
/// </summary>
public static class Palette
{
    public enum Mode
    {
        Light,
        Dark,
        HighContrast,
    }

    private static ResourceDictionary? _colors;
    private static ResourceDictionary? _theme;

    public static Mode Current { get; private set; }

    /// <summary>What Windows asks for right now.</summary>
    public static Mode FromWindows()
    {
        if (SystemParameters.HighContrast)
            return Mode.HighContrast;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is 0 ? Mode.Dark : Mode.Light;
    }

    /// <summary>
    /// Puts the colors and the styles built on them into the app's resources. Styles look colors up once,
    /// so windows opened from then on use the new ones; a window already open keeps its look until reopened.
    /// </summary>
    public static void Apply(Mode mode)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_colors is not null)
            merged.Remove(_colors);
        if (_theme is not null)
            merged.Remove(_theme);

        Current = mode;
        _colors = Build(mode);
        // The colors go in first so the styles' StaticResource lookups find them while Theme.xaml loads.
        merged.Insert(0, _colors);
        _theme = new ResourceDictionary { Source = new Uri("pack://application:,,,/UI/Theme.xaml") };
        merged.Insert(1, _theme);
    }

    private static ResourceDictionary Build(Mode mode)
    {
        var (bg, card, line, sand, ink, mute, clay, sage, terra, terraSoft, onClay) = mode switch
        {
            Mode.Dark => (C("#1E1915"), C("#29231E"), C("#3E352C"), C("#332B24"), C("#EEE6DA"), C("#A69684"),
                C("#C48D61"), C("#97A882"), C("#E0916A"), C("#3B2820"), C("#1E1915")),
            Mode.HighContrast => (SystemColors.WindowColor, SystemColors.WindowColor, SystemColors.WindowTextColor,
                SystemColors.HighlightColor, SystemColors.WindowTextColor, SystemColors.GrayTextColor,
                SystemColors.HighlightColor, SystemColors.HighlightColor, SystemColors.HotTrackColor,
                SystemColors.WindowColor, SystemColors.HighlightTextColor),
            _ => (C("#F5EFE6"), C("#FBF8F3"), C("#E6DCCB"), C("#EDE4D6"), C("#3B2F26"), C("#8A7A69"),
                C("#8B5E3C"), C("#7D8B6A"), C("#B5653F"), C("#F3E1D6"), C("#FBF8F3")),
        };

        var colors = new ResourceDictionary
        {
            ["CardColor"] = card,
            ["LineColor"] = line,
            ["ClayColor"] = clay,
            // The card color fully see-through: rows fade their background in from it on hover.
            ["CardClearColor"] = Color.FromArgb(0, card.R, card.G, card.B),
        };
        foreach (var (key, color) in new[]
                 {
                     ("BgBrush", bg), ("CardBrush", card), ("LineBrush", line), ("SandBrush", sand),
                     ("InkBrush", ink), ("MuteBrush", mute), ("ClayBrush", clay), ("SageBrush", sage),
                     ("TerraBrush", terra), ("TerraSoftBrush", terraSoft), ("OnClayBrush", onClay),
                 })
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            colors[key] = brush;
        }
        return colors;
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
