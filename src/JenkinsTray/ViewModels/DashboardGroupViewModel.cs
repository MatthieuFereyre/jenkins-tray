using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace JenkinsTray.ViewModels;

/// <summary>
/// The cards of one Jenkins server on the dashboard, under a header that folds them away.
/// </summary>
/// <remarks>
/// With a single server on the dashboard there is one group and no header at all: grouping one
/// list under its own name only pushes the cards down. The header also has to keep telling what
/// it hides — a failure must not vanish into a folded group — hence the summary and the flags.
/// </remarks>
public partial class DashboardGroupViewModel : ObservableObject
{
    private readonly Action<DashboardGroupViewModel> _expandedChanged;

    public DashboardGroupViewModel(string serverId, Action<DashboardGroupViewModel> expandedChanged, bool isExpanded)
    {
        ServerId = serverId;
        _expandedChanged = expandedChanged;
        _isExpanded = isExpanded;
    }

    /// <summary>The server's id, not its name: a renamed server keeps its folded state.</summary>
    public string ServerId { get; }

    public ObservableCollection<MonitoredJobViewModel> Jobs { get; } = [];

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _hasFailure;
    [ObservableProperty] private bool _isBuilding;

    /// <summary>False when this is the only group: no header, and the cards always shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsJobs))]
    private bool _showHeader;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsJobs))]
    private bool _isExpanded;

    /// <summary>A lone group cannot be folded, whatever was saved for it while there were two.</summary>
    public bool ShowsJobs => IsExpanded || !ShowHeader;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    partial void OnIsExpandedChanged(bool value) => _expandedChanged(this);
}
