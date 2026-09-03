using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace JenkinsTray.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not Visibility.Visible;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>Visible when the bound string has content.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Uppercases a label and lays its letters out with hair spaces. WPF has no letter-spacing, and the
/// design leans on it for the small uppercase labels — section headings and status badges.
/// </summary>
public sealed class UpperTrackingConverter : IValueConverter
{
    private const string HairSpace = "\u200A";

    /// <summary>
    /// Spaces the letters out, and joins the words with <paramref name="wordSeparator"/>.
    /// A hair space is a line-break opportunity like any other, so a label given to a box narrower
    /// than itself would break inside a word: where that can happen, the caller stacks the words on
    /// their own lines rather than leaving the choice to WPF.
    /// </summary>
    public static string Letterspace(string? text, CultureInfo culture, string wordSeparator)
    {
        var words = (text ?? string.Empty).ToUpper(culture).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(wordSeparator, words.Select(word => string.Join(HairSpace, word.Select(c => c.ToString()))));
    }

    /// <summary>Bound labels are the status badges, whose panel is narrower than the longest of them.</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Letterspace(value as string, culture, "\n");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
