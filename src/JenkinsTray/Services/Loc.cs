using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;
using JenkinsTray.Models;

namespace JenkinsTray.Services;

/// <summary>
/// Every user-visible string, in English and French.
///
/// The two languages are separate neutral resources embedded in the main assembly rather than a
/// satellite culture pair, because the language is chosen in the settings and not by the OS: with
/// satellites, .NET resolves them from <c>CurrentUICulture</c>, and switching without a restart
/// means fighting that resolution. Here nothing is implicit — <see cref="Current"/> names the
/// table, and English is the fallback for a key a translation is missing.
///
/// XAML reaches the strings through the <see cref="TrExtension"/> markup extension, which binds to
/// the indexer, so a language change refreshes every binding at once. Log messages are deliberately
/// left out: they are diagnostics for a developer reading a file, not interface text.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly ResourceManager English =
        new("JenkinsTray.Resources.Strings_en", typeof(Loc).Assembly);

    private static readonly ResourceManager French =
        new("JenkinsTray.Resources.Strings_fr", typeof(Loc).Assembly);

    /// <summary>Single instance the XAML bindings point at.</summary>
    public static Loc Instance { get; } = new();

    private static ResourceManager _table = English;
    private static AppLanguage _current = AppLanguage.System;

    private Loc()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised after a language change, for everything a binding cannot reach: a string already
    /// composed in code and stored in a property, a tray menu built once, a list of labels.
    /// </summary>
    public static event Action? LanguageChanged;

    /// <summary>The language in effect, as chosen in the settings.</summary>
    public static AppLanguage Current => _current;

    /// <summary>
    /// Indexer the bindings use: <c>{loc:Tr Settings_Title}</c> becomes a binding on <c>[Settings_Title]</c>.
    /// A missing key returns the key itself, which shows up in the interface instead of an empty
    /// label — a silently blank control would be much harder to spot.
    /// </summary>
    public string this[string key] => T(key);

    /// <summary>Looks a key up in the current language, falling back to English.</summary>
    public static string T(string key) =>
        _table.GetString(key, CultureInfo.InvariantCulture)
        ?? English.GetString(key, CultureInfo.InvariantCulture)
        ?? key;

    /// <summary>Looks a key up and fills its placeholders, in the culture the language implies.</summary>
    public static string T(string key, params object?[] args) =>
        string.Format(FormatCulture, T(key), args);

    /// <summary>
    /// Picks between a singular and a plural key. English and French do not agree on what takes an
    /// "s" — "3 skipped" against "3 ignorés" — so the choice is a pair of keys per language rather
    /// than a suffix rule applied to one.
    /// </summary>
    public static string Plural(int count, string singularKey, string pluralKey) =>
        T(count > 1 ? pluralKey : singularKey, count);

    /// <summary>
    /// Culture used to format numbers and dates. It follows the chosen language: an interface in
    /// English that writes "1,5 %" reads as a bug, so the two move together.
    /// </summary>
    public static CultureInfo FormatCulture { get; private set; } = CultureInfo.CurrentCulture;

    /// <summary>
    /// Switches language. <see cref="AppLanguage.System"/> takes French only for a French Windows;
    /// every other OS language gets English, which is also the fallback for a missing key.
    /// </summary>
    public static void Apply(AppLanguage language)
    {
        _current = language;

        var french = language switch
        {
            AppLanguage.French => true,
            AppLanguage.English => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                    .Equals("fr", StringComparison.OrdinalIgnoreCase),
        };

        _table = french ? French : English;
        FormatCulture = french ? new CultureInfo("fr-FR") : new CultureInfo("en-US");

        // Anything formatting outside a binding — Humanize, a toast built on a background thread —
        // reads the ambient culture, so it is moved too rather than threaded through every call.
        CultureInfo.DefaultThreadCurrentCulture = FormatCulture;
        CultureInfo.DefaultThreadCurrentUICulture = FormatCulture;

        // Binding.IndexerName is the one property name that invalidates every indexer binding.
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(Binding.IndexerName));
        LanguageChanged?.Invoke();
    }
}

/// <summary>
/// <c>{loc:Tr Some_Key}</c> in XAML. Produces a one-way binding on <see cref="Loc.Instance"/>
/// rather than a plain string, which is what lets a language change reach the interface without a
/// restart.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        };

        // Handing the binding its own ProvideValue is what makes the extension work in both places
        // it is used: directly on a property, and inside a style or a template.
        return binding.ProvideValue(serviceProvider);
    }
}
