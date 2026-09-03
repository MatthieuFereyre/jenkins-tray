using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JenkinsTray.Models;
using JenkinsTray.Services;

namespace JenkinsTray.ViewModels;

/// <summary>One entry of the dashboard's sort selector.</summary>
/// <summary>
/// A sort criterion and the key naming it. The label is resolved on read rather than stored, so a
/// language change only has to re-raise it — the list itself never has to be rebuilt.
/// </summary>
public sealed class SortOption(DashboardSort key, string resourceKey) : ObservableObject
{
    public DashboardSort Key { get; } = key;

    public string Label => Loc.T(resourceKey);

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

public partial class DashboardViewModel : ObservableObject
{
    private readonly Workspace _workspace;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;

    public DashboardViewModel(Workspace workspace)
    {
        _workspace = workspace;
        _workspace.Monitoring.StatesChanged += OnStatesChanged;
        _workspace.SettingsChanged += OnStatesChanged;

        // The sort labels and the summary line are composed in code, so no binding carries them
        // over a language change: they are re-read here.
        Loc.LanguageChanged += OnLanguageChanged;

        // Backing field, not the property: restoring the saved choice must not write it back.
        _selectedSort = SortOptions.FirstOrDefault(o => o.Key == workspace.Settings.DashboardSort) ?? SortOptions[0];
    }

    private void OnLanguageChanged() => _dispatcher.Invoke(() =>
    {
        foreach (var option in SortOptions)
            option.RefreshLabel();

        Sync();
    });

    public ObservableCollection<MonitoredJobViewModel> Jobs { get; } = [];

    /// <summary>Each label states the direction its criterion reads in — there is no other.</summary>
    public IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new(DashboardSort.ServerThenName, "Sort_ServerThenName"),
        new(DashboardSort.Name, "Sort_Name"),
        new(DashboardSort.Status, "Sort_Status"),
        new(DashboardSort.LastBuild, "Sort_LastBuild"),
        new(DashboardSort.Duration, "Sort_Duration"),
        new(DashboardSort.Coverage, "Sort_Coverage"),
    ];

    [ObservableProperty] private SortOption _selectedSort;

    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _lastRefreshText = Loc.T("Dashboard_NotRefreshedYet");
    [ObservableProperty] private string _summaryText = string.Empty;

    /// <summary>Picking a criterion reorders the cards at once, and the choice outlives the session.</summary>
    partial void OnSelectedSortChanged(SortOption value)
    {
        _workspace.Settings.DashboardSort = value.Key;
        _workspace.SavePreferences();

        Sync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _workspace.Monitoring.RefreshNowAsync().ConfigureAwait(false);
    }

    private void OnStatesChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(Sync);

    /// <summary>Reconciles the card list with the poller's state, in place, keeping scroll and focus.</summary>
    public void Sync()
    {
        var states = Order(_workspace.Monitoring.States);
        var keys = states.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        for (var i = Jobs.Count - 1; i >= 0; i--)
            if (!keys.Contains(Jobs[i].Key))
                Jobs.RemoveAt(i);

        var existing = Jobs.ToDictionary(j => j.Key, StringComparer.Ordinal);

        for (var i = 0; i < states.Count; i++)
        {
            if (existing.TryGetValue(states[i].Key, out var vm))
            {
                vm.Update(states[i]);

                var index = Jobs.IndexOf(vm);
                if (index != i)
                    Jobs.Move(index, i);
            }
            else
            {
                Jobs.Insert(i, new MonitoredJobViewModel(states[i], _workspace));
            }
        }

        IsEmpty = Jobs.Count == 0;
        IsRefreshing = _workspace.Monitoring.IsRefreshing;

        LastRefreshText = _workspace.Monitoring.LastRefresh is { } last
            ? Loc.T("Dashboard_RefreshedAt", Humanize.RelativeTime(last))
            : Loc.T("Dashboard_NotRefreshedYet");

        SummaryText = BuildSummary();
    }

    /// <summary>
    /// Applies the chosen criterion, each in the single direction that makes it useful — worst,
    /// most recent or longest first — which is what its label announces. Ties always fall back to
    /// the qualified name, so the order never wobbles between two refreshes.
    /// </summary>
    private IReadOnlyList<MonitoredJobState> Order(IReadOnlyList<MonitoredJobState> states)
    {
        var byName = StringComparer.CurrentCultureIgnoreCase;

        var ordered = SelectedSort.Key switch
        {
            DashboardSort.Name =>
                states.OrderBy(s => s.QualifiedName, byName),

            DashboardSort.Status =>
                states.OrderByDescending(s => StatusMap.Severity(s.Snapshot?.Status ?? BuildStatus.Unknown)),

            DashboardSort.LastBuild =>
                states.OrderByDescending(s => s.Snapshot?.LastCompletedBuild?.Timestamp ?? DateTimeOffset.MinValue),

            DashboardSort.Duration =>
                states.OrderByDescending(s => s.Snapshot?.LastCompletedBuild?.Duration ?? TimeSpan.Zero),

            // Jobs without any coverage sit at the end rather than at the bottom of the scale.
            DashboardSort.Coverage =>
                states.OrderBy(s => s.Coverage?.LinePercent ?? s.Coverage?.BranchPercent ?? double.MaxValue),

            _ => states.OrderBy(s => s.ServerName, byName),
        };

        return [.. ordered.ThenBy(s => s.QualifiedName, byName)];
    }

    private string BuildSummary()
    {
        if (Jobs.Count == 0)
            return string.Empty;

        var failures = Jobs.Count(j => j.Status == BuildStatus.Failure);
        var unstable = Jobs.Count(j => j.Status == BuildStatus.Unstable);
        var building = Jobs.Count(j => j.IsBuilding);

        var parts = new List<string>
        {
            Loc.Plural(Jobs.Count, "Dashboard_MonitoredJobOne", "Dashboard_MonitoredJobMany"),
        };

        if (failures > 0)
            parts.Add(Loc.T("Dashboard_FailingCount", failures));
        if (unstable > 0)
            parts.Add(Loc.Plural(unstable, "Dashboard_UnstableOne", "Dashboard_UnstableMany"));
        if (building > 0)
            parts.Add(Loc.T("Dashboard_BuildingCount", building));

        return string.Join(" · ", parts);
    }
}
