using System;
using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace BatPlayer.Localization;

/// <summary>
/// App localization. Strings live in BatPlayer.Resources.Strings (Strings.resx,
/// Strings.en.resx, Strings.ru.resx). Language switches on the fly, without restart.
/// XAML: {Binding [KeyName], Source={x:Static loc:Loc.T}}; code-behind: Loc.Get(key).
/// </summary>
public static class Loc
{
    private static readonly ResourceManager _rm =
        new("BatPlayer.Resources.Strings", typeof(Loc).Assembly);

    private static string _currentCode = string.Empty;
    private static CultureInfo _culture = CultureInfo.InvariantCulture;

    /// <summary>Observable string source for XAML bindings.</summary>
    public static Localizer T { get; } = new();

    /// <summary>Culture applied by the last SetLanguage call.</summary>
    public static CultureInfo CurrentCulture => _culture;

    /// <summary>Raised after a language change (rebuild tray menu, VM titles, etc.).</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// Applies a language (e.g. "en" or "ru"): sets CurrentUICulture and notifies all
    /// {Binding [Key], Source=Loc.T} via PropertyChanged("Item[]").
    /// Safe to call again with the same code — no notifications are raised.
    /// </summary>
    public static void SetLanguage(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) code = "en";
        if (string.Equals(code, _currentCode, StringComparison.OrdinalIgnoreCase)) return;

        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(code); }
        catch (CultureNotFoundException) { culture = CultureInfo.GetCultureInfo("en"); }

        _currentCode = code;
        _culture = culture;
        CultureInfo.CurrentUICulture = culture;

        LanguageChanged?.Invoke(null, EventArgs.Empty);
        T.OnAllChanged();
    }

    /// <summary>Localized string by key; falls back to the key when missing.</summary>
    public static string Get(string key, CultureInfo? culture = null)
        => _rm.GetString(key, culture ?? _culture)
           ?? _rm.GetString(key, CultureInfo.InvariantCulture)
           ?? key;

    /// <summary>
    /// String source for XAML bindings like {Binding [Key], Source={x:Static loc:Loc.T}}.
    /// Lives as long as the app; on a language change it raises PropertyChanged("Item[]"),
    /// after which WPF re-reads all indexer bindings.
    /// </summary>
    public sealed class Localizer : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string this[string key] => Get(key);

        internal void OnAllChanged()
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}
