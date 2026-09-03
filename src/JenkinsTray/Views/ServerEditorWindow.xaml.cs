using System.Windows;
using JenkinsTray.Models;
using JenkinsTray.ViewModels;
using Wpf.Ui.Controls;

namespace JenkinsTray.Views;

public partial class ServerEditorWindow : FluentWindow
{
    private readonly ServerEditorViewModel _viewModel;

    public ServerEditorWindow(ServerConfig config, bool isNew)
    {
        _viewModel = new ServerEditorViewModel(config, isNew);
        DataContext = _viewModel;
        InitializeComponent();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryApply(out var error))
        {
            _viewModel.TestFailed = true;
            _viewModel.TestMessage = error;
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
