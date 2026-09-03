using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace JenkinsTray.Services;

/// <summary>
/// Keeps the colours of the application on the theme in force. The design is specified in dark; its
/// light counterpart lives in a twin dictionary with the same keys, and only the dictionary is
/// swapped — the styles that consume it reference every colour through DynamicResource, so nothing
/// else has to be rebuilt. Also pins the accent, which WPF-UI would otherwise take from Windows.
/// </summary>
public static class CardPaletteService
{
    private const string DarkSource = "pack://application:,,,/JenkinsTray;component/Themes/CardPalette.Dark.xaml";
    private const string LightSource = "pack://application:,,,/JenkinsTray;component/Themes/CardPalette.Light.xaml";

    /// <summary>Tells one palette dictionary from every other merged dictionary.</summary>
    private const string Marker = "CardPalette.";

    private static bool _subscribed;

    /// <summary>Follows the theme from now on, and lines the palette up with the current one.</summary>
    public static void Start()
    {
        if (!_subscribed)
        {
            ApplicationThemeManager.Changed += (theme, _) => Apply(theme);
            _subscribed = true;
        }

        Apply(ApplicationThemeManager.GetAppTheme());
    }

    /// <summary>High contrast keeps the dark palette: it is the one the whole app is drawn in.</summary>
    private static void Apply(ApplicationTheme theme)
    {
        var palette = SwapPalette(theme == ApplicationTheme.Light ? LightSource : DarkSource);
        ForceAccent(theme, palette);
    }

    private static ResourceDictionary? SwapPalette(string source)
    {
        var wanted = new Uri(source);

        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null)
            return null;

        for (var i = 0; i < dictionaries.Count; i++)
        {
            var current = dictionaries[i].Source;
            if (current is null || !current.OriginalString.Contains(Marker, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!current.OriginalString.Equals(wanted.OriginalString, StringComparison.OrdinalIgnoreCase))
                dictionaries[i] = new ResourceDictionary { Source = wanted };

            return dictionaries[i];
        }

        return null;
    }

    /// <summary>
    /// The keys WPF-UI paints its check boxes, toggles, rings and primary buttons with. It writes
    /// them straight into <c>Application.Resources</c>, where a merged dictionary cannot reach them,
    /// and refreshes them from the Windows personalisation colour every time a theme is applied —
    /// which the system theme watcher does on its own, in a real window.
    /// </summary>
    private static readonly string[] AccentKeys =
    [
        "SystemAccentColorPrimaryBrush",
        "SystemAccentColorSecondaryBrush",
        "SystemAccentColorTertiaryBrush",
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush",
        "AccentTextFillColorPrimaryBrush",
        "AccentTextFillColorSecondaryBrush",
        "TextOnAccentFillColorPrimaryBrush",
        "TextOnAccentFillColorSecondaryBrush",
        "AccentTextFillColorTertiaryBrush",
        "AccentButtonBackground",
        "AccentButtonBackgroundPointerOver",
        "AccentButtonBackgroundPressed",
        "AccentButtonForeground",
        "AccentButtonForegroundPointerOver",
        "AccentButtonForegroundPressed",
    ];

    /// <summary>
    /// The keys WPF-UI derives from the accent for one control each. Its derivation lightens the
    /// colour in the dark theme, which is the Windows convention but leaves the window with two
    /// blues: the one the design names, and a paler one on every check box and toggle. They all get
    /// the accent itself. Anything not listed here still comes from our colour, only shaded.
    /// </summary>
    private static readonly string[] ControlAccentKeys =
    [
        "CheckBoxCheckBackgroundFillChecked",
        "ToggleSwitchFillOn",
        "ToggleSwitchStrokeOn",
        "ToggleButtonBackgroundChecked",
        "RadioButtonOuterEllipseCheckedStroke",
        "ProgressBarForeground",
        "ProgressRingForegroundThemeBrush",
        "TextControlFocusedBorderBrush",
        "TreeViewItemSelectionIndicatorForeground",
        "ListViewItemPillFillBrush",
        "ComboBoxItemPillFillBrush",
        "NavigationViewSelectionIndicatorForeground",
        "SliderThumbBackground",
    ];

    /// <summary>
    /// Pins the accent of the application over the one Windows would impose. Two steps, and both are
    /// needed: handing our colour to the manager makes the whole family it derives — disabled
    /// states, elevation borders — come from our blue, then copying the palette over the top fixes
    /// the shades the design actually names, which its derivation would not land on.
    /// </summary>
    private static void ForceAccent(ApplicationTheme theme, ResourceDictionary? palette)
    {
        var resources = Application.Current?.Resources;
        if (resources is null || palette is null)
            return;

        if (palette["AccentBrush"] is not SolidColorBrush accent)
            return;

        ApplicationAccentColorManager.Apply(accent.Color, theme);

        foreach (var key in AccentKeys)
            if (palette[key] is { } value)
                resources[key] = value;

        foreach (var key in ControlAccentKeys)
            resources[key] = accent;
    }
}
