using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using BatPlayer.Models;

namespace BatPlayer.Services;

/// <summary>
/// Применение темы и акцентного цвета: перезаписывает Color/Brush-ресурсы приложения
/// ДО создания MainWindow — все {StaticResource} в шаблонах резолвятся при загрузке
/// окна и подхватывают новые значения без перезапуска.
///
/// Режимы: "standard" — палитра Colors.xaml как есть; "darker" — интерфейс ещё темнее
/// (фон/панели/границы уведены к чёрному, текст не тронут); "light" — светлая.
///
/// ВАЖНО: кисти мутируются НА МЕСТЕ. Стили DarkTheme захватили ссылки на экземпляры
/// кистей при первом парсинге (StaticResource резолвится один раз), поэтому замена
/// ресурса на новую кисть до стилей не доходит — а изменение Color у существующего
/// экземпляра обновляет всех, кто на него ссылается. Словарь держит кисти unfrozen
/// именно для этого.
/// </summary>
public static class ThemeService
{
    /// <summary>Пресеты акцентного цвета: ключ → (Accent, AccentDim).</summary>
    public static readonly (string Key, string Label, string Accent, string Dim)[] Accents =
    [
        ("default", "Default", "#E8E8E8", "#888888"),
        ("blue",    "Blue",    "#4A9EFF", "#2A5C99"),
        ("green",   "Green",   "#35C75A", "#1F7A38"),
        ("purple",  "Purple",  "#9B6DFF", "#5C3F99"),
        ("red",     "Red",     "#FF5A5A", "#993636"),
        ("orange",  "Orange",  "#FFB13D", "#996A25")
    ];

    /// <summary>Палитра «ещё темнее»: цвета фона/панелей/границ уведены к чёрному.</summary>
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

    /// <summary>Светлая палитра: тёмный текст на светлых поверхностях.</summary>
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

    /// <summary>Пары цвет-ресурс → кисть-ресурс (кисти мутируются на месте).</summary>
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

    /// <summary>Базовая палитра Colors.xaml, снятая при первом Apply, ПОКА ресурсы
    /// ещё не тронуты. Режимы — дельты поверх неё: раньше «standard» был пустой
    /// дельтой и не возвращал исходные цвета, из-за чего после light/darker текст
    /// оставался тёмным, а переключения наслаивались друг на друга.</summary>
    private static Dictionary<string, Color>? _baseColors;

    /// <summary>Применить тему из настроек (вызывать до создания MainWindow и по «Применить»).</summary>
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

        // 1) полный сброс к базовой палитре, 2) дельта выбранного режима поверх.
        foreach (var (colorKey, color) in _baseColors)
            res[colorKey] = color;
        foreach (var (key, hex) in palette)
            res[key] = (Color)ColorConverter.ConvertFromString(hex);

        // Акцент: пресет пользователя; "default" в светлой теме темнеет (серый не виден).
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
        // Сердечки тоже под акцент (платформенные логотипы остаются собой).
        res["FavoriteColor"] = (Color)ColorConverter.ConvertFromString(accent.Accent);

        // Кисти: ВАЖНО — кисти из BAML-словарей приходят ЗАМОРОЖЕННЫМИ (frozen),
        // мутировать их нельзя. Замороженные заменяются НОВЫМ экземпляром кисти,
        // поэтому все ссылки на кисти темы в XAML должны быть DynamicResource
        // (StaticResource удержал бы старый замороженный экземпляр, и тема/акцент
        // не применялись бы к уже созданным стилям — «белые» AccentButton).
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
