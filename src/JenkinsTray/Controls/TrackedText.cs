using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using JenkinsTray.Converters;

namespace JenkinsTray.Controls;

/// <summary>
/// Writes a fixed label into a <see cref="TextBlock"/> with the letter-spacing the design asks of
/// its small uppercase labels. The converter covers the bound case; this covers the many literal
/// ones, where going through a binding just to reach a converter would drown the markup.
/// These labels head a section and have the width of the page: their words stay on one line.
/// </summary>
public static class TrackedText
{
    /// <summary>A plain space is barely wider than the tracking itself: the words would run together.</summary>
    private const string EnSpace = "\u2002";

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(TrackedText), new PropertyMetadata(null, OnTextChanged));

    public static void SetText(DependencyObject element, string value) =>
        element.SetValue(TextProperty, value);

    public static string GetText(DependencyObject element) =>
        (string)element.GetValue(TextProperty);

    private static void OnTextChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is TextBlock block)
            block.Text = UpperTrackingConverter.Letterspace(e.NewValue as string, CultureInfo.CurrentCulture, EnSpace);
    }
}
