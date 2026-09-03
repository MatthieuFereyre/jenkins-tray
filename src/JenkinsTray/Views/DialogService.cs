using System.Windows;
using JenkinsTray.Models;
using JenkinsTray.Services;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace JenkinsTray.Views;

public sealed class DialogService : IDialogService
{
    public Window? Owner { get; set; }

    public bool EditServer(ServerConfig config, bool isNew)
    {
        var window = new ServerEditorWindow(config, isNew);

        if (Owner is { IsVisible: true })
            window.Owner = Owner;
        else
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return window.ShowDialog() == true;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmLabel,
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = Loc.T("Editor_Cancel"),
        };

        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }
}
