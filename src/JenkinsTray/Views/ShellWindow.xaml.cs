using System.ComponentModel;
using System.Windows;
using JenkinsTray.Services;
using JenkinsTray.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace JenkinsTray.Views;

public partial class ShellWindow : FluentWindow
{
    private readonly Workspace _workspace;

    public ShellWindow(ShellViewModel viewModel, Workspace workspace)
    {
        _workspace = workspace;
        ViewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();
        ThemeService.Track(this);
    }

    public ShellViewModel ViewModel { get; }

    /// <summary>Set by the app when the user really wants to quit, so closing is not intercepted.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && _workspace.Settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);

        // With "close to the notification area" disabled, closing the window means quitting: keeping the app
        // alive would leave a tray icon whose window can no longer be shown.
        if (!e.Cancel && !AllowClose)
            Application.Current.Shutdown();
    }

    public void ShowAndActivate(int? pageIndex = null)
    {
        if (pageIndex is int index)
            ViewModel.Navigate(index);

        Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }
}
