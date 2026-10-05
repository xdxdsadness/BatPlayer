using System;
using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace BatPlayer.Localization;

/// <summary>
/// Локализация приложения. Строки лежат в BatPlayer.Resources.Strings (Strings.resx,
/// Strings.en.resx, Strings.ru.resx). Смена языка — на лету, без перезапуска.
/// XAML: {Binding [KeyName], Source={x:Static loc:Loc.T}}; code-behind: Loc.Get(key).
/// </summary>
public static class Loc
{
    private static readonly ResourceManager _rm =
        new("BatPlayer.Resources.Strings", typeof(Loc).Assembly);

    private static string _currentCode = string.Empty;
    private static CultureInfo _culture = CultureInfo.InvariantCulture;

    /// <summary>Наблюдаемый источник строк для XAML-биндингов.</summary>
    public static Localizer T { get; } = new();

    /// <summary>Культура, применённая последним вызовом SetLanguage.</summary>
    public static CultureInfo CurrentCulture => _culture;

    /// <summary>Событие после смены языка (перестроить трей-меню, заголовки VM и т.п.).</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// Применяет язык (например "en" или "ru"): выставляет CurrentUICulture,
    /// уведомляет все {Binding [Key], Source=Loc.T} через PropertyChanged("Item[]").
    /// Безопасно вызывать повторно с тем же кодом — уведомлений не будет.
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

    /// <summary>Локализованная строка по ключу; при отсутствии — ключ.</summary>
    public static string Get(string key, CultureInfo? culture = null)
        => _rm.GetString(key, culture ?? _culture)
           ?? _rm.GetString(key, CultureInfo.InvariantCulture)
           ?? key;

    /// <summary>
    /// Источник строк для XAML-биндингов вида {Binding [Key], Source={x:Static loc:Loc.T}}.
    /// Живёт столько же, сколько приложение; при смене языка кидает PropertyChanged("Item[]"),
    /// после чего WPF перечитывает все индексаторные биндинги.
    /// </summary>
    public sealed class Localizer : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string this[string key] => Get(key);

        internal void OnAllChanged()
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}
