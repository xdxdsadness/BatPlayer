using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using BatPlayer.Models;

namespace BatPlayer.Services;

/// <summary>
/// Applies the theme and accent color: overwrites the app's Color/Brush resources
/// BEFORE MainWindow is created — all {StaticResource} in templates resolve at
/// window load and pick up the new values without a restart.
///
/// Modes: "standard" — the Colors.xaml palette as-is; "darker" — an even darker UI
/// (background/panels/borders pushed toward black, text untouched); "light" — light.
///
/// IMPORTANT: brushes are mutated IN PLACE. The DarkTheme styles captured references
/// to the brush instances at first parse (StaticResource resolves once), so replacing
/// a resource with a new brush never reaches the styles — but changing the Color of
/// an existing instance updates everything referencing it. The dictionary keeps the
/// brushes unfrozen exactly for this.
/// </summary>
public static class ThemeService
{
    /// <summary>Accent color presets: key → (Accent, AccentDim).</summary>
    public static readonly (string Key, string Label, string Accent, string Dim)[] Accents =
    [
        ("default", "Default", "#E8E8E8", "#888888"),
        ("blue",    "Blue",    "#4A9EFF", "#2A5C99"),
        ("green",   "Green",   "#35C75A", "#1F7A38"),
        ("purple",  "Purple",  "#9B6DFF", "#5C3F99"),
        ("red",     "Red",     "#FF5A5A", "#993636"),
        ("orange",  "Orange",  "#FFB13D", "#996A25")
    ];

    /// <summary>The "darker" palette: background/panel/border colors pushed toward black.</summary>
    private static readonly (string Key, string Hex)[] DarkerColors =
    [
        ("BgColor", "#060606"),
        ("PanelColor", "#0B0B0B"),
        ("PanelElevatedColor", "#101010"),
        ("SecondaryColor", "#161616"),
        ("SecondaryElevatedColor", "#1C1C1C"),
        ("BorderColor", "#222222"),
        ("BorderLightColor", "#2C2C2C"),
        ("HoverColor", "#2A2A2A"),
        ("PressedColor", "#141414"),
        ("SelectedColor", "#1F1F1F"),
        ("FocusColor", "#333333")
    ];

    /// <summary>Light palette: dark text on light surfaces.</summary>
    private static readonly (string Key, string Hex)[] LightColors =
    [
        ("BgColor", "#F2F2F5"),
        ("PanelColor", "#FFFFFF"),
        ("PanelElevatedColor", "#FFFFFF"),
        ("SecondaryColor", "#E7E7EC"),
        ("SecondaryElevatedColor", "#DCDCE3"),
        ("BorderColor", "#D5D5DC"),
        ("BorderLightColor", "#C4C4CD"),
        ("TextPrimaryColor", "#17171C"),
        ("TextSecondaryColor", "#5A5A66"),
        ("TextInactiveColor", "#A0A0AA"),
        ("HoverColor", "#E2E2E9"),
        ("PressedColor", "#D8D8DF"),
        ("SelectedColor", "#E8E8F0"),
        ("FocusColor", "#B9B9C4"),
        ("AccentColor", "#2A2A2E"),
        ("AccentDimColor", "#808088")
    ];

    /// <summary>Color-resource → brush-resource pairs (brushes are mutated in place).</summary>
    private static readonly (string ColorKey, string BrushKey)[] ColorBrushPairs =
    [
        ("BgColor", "BgBrush"),
        ("PanelColor", "PanelBrush"),
        ("PanelElevatedColor", "PanelElevatedBrush"),
        ("SecondaryColor", "SecondaryBrush"),
        ("SecondaryElevatedColor", "SecondaryElevatedBrush"),
        ("BorderColor", "BorderBrush"),
        ("BorderLightColor", "BorderLightBrush"),
        ("TextPrimaryColor", "TextPrimaryBrush"),
        ("TextSecondaryColor", "TextSecondaryBrush"),
        ("TextInactiveColor", "TextInactiveBrush"),
        ("AccentColor", "AccentBrush"),
        ("AccentDimColor", "AccentDimBrush"),
        ("FavoriteColor", "FavoriteBrush"),
        ("HoverColor", "HoverBrush"),
        ("PressedColor", "PressedBrush"),
        ("SelectedColor", "SelectedBrush"),
        ("FocusColor", "FocusBrush")
    ];

    /// <summary>Base Colors.xaml palette captured at the first Apply, WHILE the
    /// resources are still untouched. Modes are deltas over it: "standard" used to be
    /// an empty delta and never restored the original colors, so after light/darker
    /// the text stayed dark and switches stacked on top of each other.</summary>
    private static Dictionary<string, Color>? _baseColors;

    /// <summary>Applies the theme from settings (call before MainWindow is created and on "Apply").</summary>
    public static void Apply(AppSettings settings)
    {
        var res = Application.Current.Resources;

        if (_baseColors == null)
        {
            _baseColors = new Dictionary<string, Color>();
            foreach (var (colorKey, _) in ColorBrushPairs)
                if (res[colorKey] is Color color) _baseColors[colorKey] = color;
        }

        IReadOnlyList<(string Key, string Hex)> palette = settings.ThemeMode switch
        {
            "light" => LightColors,
            "darker" => DarkerColors,
            _ => Array.Empty<(string, string)>()
        };

        // 1) full reset to the base palette, 2) delta of the selected mode on top.
        foreach (var (colorKey, color) in _baseColors)
            res[colorKey] = color;
        foreach (var (key, hex) in palette)
            res[key] = (Color)ColorConverter.ConvertFromString(hex);

        // Accent: the user's preset; "default" darkens in the light theme (gray is invisible).
        var accentKey = settings.AccentColor;
        if (accentKey == "default" && string.Equals(settings.ThemeMode, "light", StringComparison.Ordinal))
            accentKey = "darkdefault";
        var accent = Array.Find(Accents, a => a.Key == accentKey);
        if (accent.Key == null)
        {
            accent = accentKey == "darkdefault"
                ? ("darkdefault", "Default", "#2A2A2E", "#808088")
                : Accents[0];
        }
        res["AccentColor"] = (Color)ColorConverter.ConvertFromString(accent.Accent);
        res["AccentDimColor"] = (Color)ColorConverter.ConvertFromString(accent.Dim);
        // Hearts follow the accent too (platform logos stay themselves).
        res["FavoriteColor"] = (Color)ColorConverter.ConvertFromString(accent.Accent);

        // Brushes: IMPORTANT — brushes from BAML dictionaries arrive FROZEN and must
        // not be mutated. Frozen ones are replaced with a NEW brush instance, so all
        // theme brush references in XAML must be DynamicResource (StaticResource would
        // hold the old frozen instance and the theme/accent would never apply to
        // already-created styles — the "white" AccentButton).
        foreach (var (colorKey, brushKey) in ColorBrushPairs)
        {
            if (res[colorKey] is not Color color) continue;
            if (res[brushKey] is SolidColorBrush brush && !brush.IsFrozen)
            {
                brush.Color = color;
            }
            else
            {
                res[brushKey] = new SolidColorBrush(color);
            }
        }
    }
}
