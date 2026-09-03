using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JenkinsTray.Models;
using JenkinsTray.Services;

namespace JenkinsTray.ViewModels;

/// <summary>The job explorer: pick a server, browse its folders, tick what you want to watch.</summary>
public partial class JobsViewModel : ObservableObject
{
    private readonly Workspace _workspace;
    private bool _suppressCheckHandling;
    private CancellationTokenSource? _loadCts;
    private string? _totalJobsMessage;

    public JobsViewModel(Workspace workspace)
    {
        _workspace = workspace;
        _workspace.SettingsChanged += (_, _) => RefreshCheckedState();
    }

    public ObservableCollection<ServerConfig> Servers { get; } = [];

    public ObservableCollection<JobNodeViewModel> Nodes { get; } = [];

    [ObservableProperty] private ServerConfig? _selectedServer;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _hasServers;
    [ObservableProperty] private int _monitoredCount;
    [ObservableProperty] private int _visibleJobCount;

    /// <summary>Called when the page becomes visible: refresh the server list, load once.</summary>
    public void OnActivated()
    {
        SyncServers();

        if (SelectedServer is not null && Nodes.Count == 0)
            LoadCommand.Execute(null);
    }

    private void SyncServers()
    {
        var previousId = SelectedServer?.Id;

        Servers.Clear();
        foreach (var server in _workspace.Settings.Servers)
            Servers.Add(server);

        HasServers = Servers.Count > 0;

        var restored = previousId is null ? null : Servers.FirstOrDefault(s => s.Id == previousId);
        SelectedServer = restored ?? Servers.FirstOrDefault();
    }

    partial void OnSelectedServerChanged(ServerConfig? value)
    {
        Nodes.Clear();
        Message = null;
        HasError = false;
        UpdateCounts();

        if (value is not null)
            LoadCommand.Execute(null);
    }

    partial void OnSearchTextChanged(string value)
    {
        foreach (var node in Nodes)
            node.ApplyFilter(value);

        foreach (var node in Nodes)
            node.RefreshFolderStates();

        UpdateCounts();

        Message = string.IsNullOrWhiteSpace(value)
            ? _totalJobsMessage
            : $"{VisibleJobCount} job{(VisibleJobCount > 1 ? "s" : "")} correspondant{(VisibleJobCount > 1 ? "s" : "")} au filtre.";
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var server = SelectedServer;
        if (server is null)
            return;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        IsLoading = true;
        HasError = false;
        Message = null;

        try
        {
            var client = _workspace.Monitoring.GetClient(server);
            var root = await client.GetJobTreeAsync(_workspace.Settings.DiscoveryDepth, ct).ConfigureAwait(true);

            var monitored = server.Jobs.Select(j => j.FullName).ToHashSet(StringComparer.Ordinal);

            _suppressCheckHandling = true;
            Nodes.Clear();

            foreach (var child in root.Children.OrderByDescending(c => c.IsFolder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
                Nodes.Add(new JobNodeViewModel(child, this, monitored));

            _suppressCheckHandling = false;

            foreach (var node in Nodes)
                node.ApplyFilter(SearchText);

            var total = Nodes.SelectMany(n => n.Descend()).Count(n => n.IsJob);
            _totalJobsMessage = total == 0
                ? Loc.T("Jobs_NoneFound")
                : Loc.Plural(total, "Jobs_FoundOne", "Jobs_FoundMany");
            Message = _totalJobsMessage;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer load.
        }
        catch (Exception ex)
        {
            _suppressCheckHandling = false;
            HasError = true;
            Message = Loc.T("Jobs_ListFailed", ex.Message);
        }
        finally
        {
            IsLoading = false;
            UpdateCounts();
        }
    }

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var node in Nodes)
            node.SetExpanded(true);
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var node in Nodes)
            node.SetExpanded(false);
    }

    [RelayCommand]
    private void CheckAll()
    {
        var server = SelectedServer;
        if (server is null)
            return;

        var jobs = VisibleJobs().Select(ToMonitoredJob).ToList();
        if (jobs.Count == 0)
            return;

        _workspace.SetMonitored(server, jobs, true);
        RefreshCheckedState();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        var server = SelectedServer;
        if (server is null || server.Jobs.Count == 0)
            return;

        // Without a filter, wipe the list outright: it may hold jobs the tree never showed —
        // renamed, deleted, or deeper than the discovery depth.
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            server.Jobs.Clear();
            _workspace.Save();
        }
        else
        {
            _workspace.SetMonitored(server, VisibleJobs().Select(ToMonitoredJob).ToList(), false);
        }

        RefreshCheckedState();
    }

    /// <summary>A job carries only itself; a folder carries every job it currently shows.</summary>
    internal void OnNodeCheckedChanged(JobNodeViewModel node, bool isChecked)
    {
        if (_suppressCheckHandling)
            return;

        var server = SelectedServer;
        if (server is null)
            return;

        var jobs = node.VisibleJobs().Select(ToMonitoredJob).ToList();
        if (jobs.Count == 0)
            return;

        _workspace.SetMonitored(server, jobs, isChecked);
        UpdateCounts();
    }

    /// <summary>Re-syncs the checkboxes after the settings changed somewhere else (dashboard, tray).</summary>
    private void RefreshCheckedState()
    {
        var server = SelectedServer;
        if (server is null)
            return;

        var monitored = server.Jobs.Select(j => j.FullName).ToHashSet(StringComparer.Ordinal);

        _suppressCheckHandling = true;
        foreach (var node in Nodes.SelectMany(n => n.Descend()).Where(n => n.IsJob))
            node.SetCheckedSilently(monitored.Contains(node.FullName));

        foreach (var node in Nodes)
            node.RefreshFolderStates();
        _suppressCheckHandling = false;

        UpdateCounts();
    }

    /// <summary>Every job the tree currently shows — the filter decides what "all" means.</summary>
    private IEnumerable<JobNodeViewModel> VisibleJobs() =>
        Nodes.Where(n => n.IsVisible).SelectMany(n => n.VisibleJobs());

    private static MonitoredJob ToMonitoredJob(JobNodeViewModel node) =>
        new() { FullName = node.FullName, DisplayName = node.Name, Path = node.Path };

    private void UpdateCounts()
    {
        MonitoredCount = SelectedServer?.Jobs.Count ?? 0;
        VisibleJobCount = Nodes.SelectMany(n => n.Descend()).Count(n => n.IsJob && n.IsVisible);
    }
}
