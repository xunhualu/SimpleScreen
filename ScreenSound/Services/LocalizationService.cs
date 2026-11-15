using System.Globalization;
using System.Windows;

namespace ScreenSound.Services;

/// <summary>
/// Runtime UI localisation. The string tables live in
/// <c>Resources/Strings.en.xaml</c> / <c>Strings.zh.xaml</c> / <c>Strings.ja.xaml</c> /
/// <c>Strings.ko.xaml</c> and are merged into <see cref="Application.Resources"/>; XAML reads them
/// through <c>{DynamicResource Key}</c>.
/// <para>
/// Switching languages swaps the merged dictionary. WPF re-resolves every
/// DynamicResource reference when a merged dictionary changes, so the whole UI
/// re-labels itself live — no window reload and no state loss (routing keeps
/// running, sliders keep their values).
/// </para>
/// <para>
/// Text produced in code (status lines, tray menu, combo-box sentinels) can't
/// use DynamicResource, so those call <see cref="Get"/> / <see cref="Format"/>
/// and subscribe to <see cref="LanguageChanged"/> to refresh themselves.
/// </para>
/// </summary>
public static class LocalizationService
{
    public const string English = "en";
    public const string Chinese = "zh";
    public const string Japanese = "ja";
    public const string Korean = "ko";

    /// <summary>Currently applied language code ("en", "zh", "ja" or "ko").</summary>
    public static string Current { get; private set; } = English;

    /// <summary>
    /// Raised after the string table has been swapped, on the UI thread.
    /// Code-generated text should be rebuilt from this handler.
    /// </summary>
    public static event Action? LanguageChanged;

    private static ResourceDictionary? _activeDictionary;

    /// <summary>
    /// Language to use when settings don't pin one: each language is selected
    /// on a Windows install whose UI language matches it, English everywhere else.
    /// </summary>
    public static string SystemDefault()
    {
        var twoLetter = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return twoLetter.ToLowerInvariant() switch
        {
            "zh" => Chinese,
            "ja" => Japanese,
            "ko" => Korean,
            _ => English
        };
    }

    /// <summary>Maps anything (null, "", "zh-CN", legacy values) onto a supported code.</summary>
    public static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return SystemDefault();

        var lower = language.ToLowerInvariant();
        if (lower.StartsWith("zh")) return Chinese;
        if (lower.StartsWith("en")) return English;
        if (lower.StartsWith("ja")) return Japanese;
        if (lower.StartsWith("ko")) return Korean;
        return SystemDefault();
    }

    /// <summary>
    /// Swaps the merged string table. Safe to call before the main window is
    /// created (App.OnStartup does exactly that, to avoid an English flash on a
    /// Chinese system) and safe to call repeatedly with the same language.
    /// </summary>
    public static void Apply(string? language)
    {
        var code = Normalize(language);

        var app = Application.Current;
        if (app == null) return;

        // Already on this table and it's still merged → nothing to do.
        if (code == Current && _activeDictionary != null
            && app.Resources.MergedDictionaries.Contains(_activeDictionary))
        {
            return;
        }

        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Resources/Strings.{code}.xaml", UriKind.Absolute)
        };

        var merged = app.Resources.MergedDictionaries;
        var previous = _activeDictionary;
        merged.Insert(0, dictionary);
        _activeDictionary = dictionary;
        Current = code;

        // Remove the old table *after* the new one is in place so no look-up
        // ever sees a moment without a value for a key.
        if (previous != null)
            merged.Remove(previous);

        LanguageChanged?.Invoke();
    }

    /// <summary>Looks up a string; returns the key itself when it's missing.</summary>
    public static string Get(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;

    /// <summary>Looks up a format string ("{0} …") and fills in the arguments.</summary>
    public static string Format(string key, params object?[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
