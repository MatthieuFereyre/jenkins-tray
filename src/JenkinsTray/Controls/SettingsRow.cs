using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace JenkinsTray.Controls;

/// <summary>
/// One line of a settings group: an icon, a label, an optional explanation, and the control that
/// changes the value. Plays the part of WPF-UI's CardControl, but drawn on the tokens of the
/// dashboard card — rows separated by a hairline inside one surface, rather than as many floating
/// cards as there are settings.
/// </summary>
public class SettingsRow : ContentControl
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol), typeof(SymbolRegular), typeof(SettingsRow),
        new PropertyMetadata(SymbolRegular.Empty));

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingsRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsRow),
        new PropertyMetadata(null, OnDescriptionChanged));

    private static readonly DependencyPropertyKey HasDescriptionKey = DependencyProperty.RegisterReadOnly(
        nameof(HasDescription), typeof(bool), typeof(SettingsRow), new PropertyMetadata(false));

    public static readonly DependencyProperty HasDescriptionProperty = HasDescriptionKey.DependencyProperty;

    public SymbolRegular Symbol
    {
        get => (SymbolRegular)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>Second line, for a setting whose name does not say enough on its own.</summary>
    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Drives the visibility of the second line from the template.</summary>
    public bool HasDescription => (bool)GetValue(HasDescriptionProperty);

    private static void OnDescriptionChanged(DependencyObject element, DependencyPropertyChangedEventArgs e) =>
        element.SetValue(HasDescriptionKey, !string.IsNullOrWhiteSpace(e.NewValue as string));
}
