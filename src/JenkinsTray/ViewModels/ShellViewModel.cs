using CommunityToolkit.Mvvm.ComponentModel;
using JenkinsTray.Services;

namespace JenkinsTray.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(Workspace workspace, IDialogService dialogs)
    {
        Dashboard = new DashboardViewModel(workspace);
        Jobs = new JobsViewModel(workspace);
        Settings = new SettingsViewModel(workspace, dialogs);

        _currentPage = Dashboard;
        Dashboard.Sync();
    }

    public DashboardViewModel Dashboard { get; }
    public JobsViewModel Jobs { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>Version of the running build, shown at the foot of the navigation rail.</summary>
    public string AppVersion { get; } = AppInfo.DisplayVersion;

    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private int _selectedIndex;

    partial void OnSelectedIndexChanged(int value)
    {
        switch (value)
        {
            case 1:
                Jobs.OnActivated();
                CurrentPage = Jobs;
                break;

            case 2:
                Settings.Reload();
                CurrentPage = Settings;
                break;

            default:
                Dashboard.Sync();
                CurrentPage = Dashboard;
                break;
        }
    }

    /// <summary>Jumps to a page from outside (tray menu, empty-state buttons).</summary>
    public void Navigate(int index) => SelectedIndex = index;
}
