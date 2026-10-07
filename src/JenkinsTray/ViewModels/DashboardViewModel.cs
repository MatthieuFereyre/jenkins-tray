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

    /// <summary>
    /// The same cards, by server. This is what the view shows: one headed, foldable group per
    /// server, or a single group without header when the dashboard only holds one server.
    /// </summary>
    public ObservableCollection<DashboardGroupViewModel> Groups { get; } = [];

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
        // Grouping starts at two servers on the dashboard; the servers then come first in the
        // order, so each one's cards are contiguous and the chosen criterion applies within it.
        var grouped = _workspace.Monitoring.States.Select(s => s.ServerId).Distinct(StringComparer.Ordinal).Count() > 1;
        var states = Order(_workspace.Monitoring.States, grouped);

        var existing = Jobs.ToDictionary(j => j.Key, StringComparer.Ordinal);
        var wanted = new List<MonitoredJobViewModel>(states.Count);
        foreach (var state in states)
        {
            if (existing.TryGetValue(state.Key, out var vm))
                vm.Update(state);
            else
                vm = new MonitoredJobViewModel(state, _workspace);
            wanted.Add(vm);
        }

        Reconcile(Jobs, wanted);
        SyncGroups(states, wanted, grouped);

        IsEmpty = Jobs.Count == 0;
        IsRefreshing = _workspace.Monitoring.IsRefreshing;

        LastRefreshText = _workspace.Monitoring.LastRefresh is { } last
            ? Loc.T("Dashboard_RefreshedAt", Humanize.RelativeTime(last))
            : Loc.T("Dashboard_NotRefreshedYet");

        SummaryText = BuildSummary(Jobs);
    }

    /// <summary>One group per server, in the order the states came, each reconciled in place.</summary>
    private void SyncGroups(IReadOnlyList<MonitoredJobState> states, IReadOnlyList<MonitoredJobViewModel> cards, bool grouped)
    {
        var existing = Groups.ToDictionary(g => g.ServerId, StringComparer.Ordinal);
        var collapsed = _workspace.Settings.CollapsedDashboardServers;
        var wanted = new List<DashboardGroupViewModel>();
        var members = new Dictionary<string, List<MonitoredJobViewModel>>(StringComparer.Ordinal);

        for (var i = 0; i < states.Count; i++)
        {
            var serverId = states[i].ServerId;
            if (!members.TryGetValue(serverId, out var list))
            {
                if (!existing.TryGetValue(serverId, out var group))
                    group = new DashboardGroupViewModel(serverId, OnGroupExpandedChanged, !collapsed.Contains(serverId));

                group.Name = states[i].ServerName;
                wanted.Add(group);
                members[serverId] = list = [];
            }
            list.Add(cards[i]);
        }

        Reconcile(Groups, wanted);

        foreach (var group in Groups)
        {
            var jobs = members[group.ServerId];
            Reconcile(group.Jobs, jobs);
            group.ShowHeader = grouped;
            group.Summary = BuildSummary(jobs);
            group.HasFailure = jobs.Any(j => j.Status == BuildStatus.Failure);
            group.IsBuilding = jobs.Any(j => j.IsBuilding);
        }
    }

    /// <summary>Folding a group is a choice that outlives the session, like the sort.</summary>
    private void OnGroupExpandedChanged(DashboardGroupViewModel group)
    {
        var collapsed = _workspace.Settings.CollapsedDashboardServers;
        var changed = group.IsExpanded
            ? collapsed.Remove(group.ServerId)
            : !collapsed.Contains(group.ServerId);
        if (!group.IsExpanded && changed)
            collapsed.Add(group.ServerId);

        if (changed)
            _workspace.SavePreferences();
    }

    /// <summary>
    /// Brings <paramref name="target"/> to <paramref name="wanted"/> by removals, moves and
    /// insertions only, never a reset, so the cards keep their scroll position and focus.
    /// </summary>
    private static void Reconcile<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted) where T : class
    {
        var keep = new HashSet<T>(wanted, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keep.Contains(target[i]))
                target.RemoveAt(i);

        for (var i = 0; i < wanted.Count; i++)
        {
            var index = target.IndexOf(wanted[i]);
            if (index < 0)
                target.Insert(i, wanted[i]);
            else if (index != i)
                target.Move(index, i);
        }
    }

    /// <summary>
    /// Applies the chosen criterion, each in the single direction that makes it useful — worst,
    /// most recent or longest first — which is what its label announces. Ties always fall back to
    /// the qualified name, so the order never wobbles between two refreshes.
    /// </summary>
    /// <remarks>
    /// When the dashboard is grouped, the server comes first and the criterion second: each
    /// group is then a contiguous run, ordered by the criterion within.
    /// </remarks>
    private IReadOnlyList<MonitoredJobState> Order(IReadOnlyList<MonitoredJobState> states, bool byServer)
    {
        var byName = StringComparer.CurrentCultureIgnoreCase;

        // Server name, then id: two servers sharing a name must still form two runs.
        var source = byServer
            ? states.OrderBy(s => s.ServerName, byName).ThenBy(s => s.ServerId, StringComparer.Ordinal)
            : states.OrderBy(_ => 0);

        var ordered = SelectedSort.Key switch
        {
            DashboardSort.Name =>
                source.ThenBy(s => s.QualifiedName, byName),

            DashboardSort.Status =>
                source.ThenByDescending(s => StatusMap.Severity(s.Snapshot?.Status ?? BuildStatus.Unknown)),

            DashboardSort.LastBuild =>
                source.ThenByDescending(s => s.Snapshot?.LastCompletedBuild?.Timestamp ?? DateTimeOffset.MinValue),

            DashboardSort.Duration =>
                source.ThenByDescending(s => s.Snapshot?.LastCompletedBuild?.Duration ?? TimeSpan.Zero),

            // Jobs without any coverage sit at the end rather than at the bottom of the scale.
            DashboardSort.Coverage =>
                source.ThenBy(s => s.Coverage?.LinePercent ?? s.Coverage?.BranchPercent ?? double.MaxValue),

            _ => source.ThenBy(s => s.ServerName, byName),
        };

        return [.. ordered.ThenBy(s => s.QualifiedName, byName)];
    }

    /// <summary>The summary line of the page, and of each group header.</summary>
    private static string BuildSummary(IReadOnlyCollection<MonitoredJobViewModel> jobs)
    {
        if (jobs.Count == 0)
            return string.Empty;

        var failures = jobs.Count(j => j.Status == BuildStatus.Failure);
        var unstable = jobs.Count(j => j.Status == BuildStatus.Unstable);
        var building = jobs.Count(j => j.IsBuilding);

        var parts = new List<string>
        {
            Loc.Plural(jobs.Count, "Dashboard_MonitoredJobOne", "Dashboard_MonitoredJobMany"),
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
