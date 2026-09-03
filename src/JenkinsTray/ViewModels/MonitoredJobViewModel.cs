using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JenkinsTray.Models;
using JenkinsTray.Services;

namespace JenkinsTray.ViewModels;

/// <summary>One card on the dashboard.</summary>
public partial class MonitoredJobViewModel : ObservableObject
{
    private readonly Workspace _workspace;
    private readonly string _serverId;

    public MonitoredJobViewModel(MonitoredJobState state, Workspace workspace)
    {
        _workspace = workspace;
        _serverId = state.ServerId;
        Key = state.Key;
        FullName = state.Job.FullName;
        Update(state);
    }

    public string Key { get; }

    public string FullName { get; }

    [ObservableProperty] private string _displayName = string.Empty;

    /// <summary>Project holding the job — the title reads "project › branch".</summary>
    [ObservableProperty] private string _projectName = string.Empty;

    /// <summary>The chevron itself, empty for a job that sits at the root and has no project above it.</summary>
    [ObservableProperty] private string _projectSeparator = string.Empty;

    [ObservableProperty] private string _serverName = string.Empty;

    /// <summary>Folders above the project — what the title does not already say.</summary>
    [ObservableProperty] private string _folderPath = string.Empty;

    /// <summary>Server, folders, age and duration on one line, under the title.</summary>
    [ObservableProperty] private string _metaLine = string.Empty;

    [ObservableProperty] private BuildStatus _status = BuildStatus.Unknown;
    [ObservableProperty] private string _statusLabel = Loc.T("Status_Unknown");
    [ObservableProperty] private bool _isBuilding;

    /// <summary>A build we asked for, not yet visible to the poller — it is still in the queue.</summary>
    [ObservableProperty] private bool _isBuildRequested;

    /// <summary>Building or about to: what makes the card pulse, and what the badge announces.</summary>
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _activityLabel = BuildingLabel;

    /// <summary>The build request is in flight — the button shows a ring in place of its glyph.</summary>
    [ObservableProperty] private bool _isTriggering;

    [ObservableProperty] private string _buildLabel = NoBuildNumber;
    [ObservableProperty] private string _timingLabel = string.Empty;

    /// <summary>Whether the card carries a bottom row at all — tests, coverage, or neither.</summary>
    [ObservableProperty] private bool _hasStats;

    [ObservableProperty] private bool _hasTests;
    [ObservableProperty] private int _testsTotal;

    /// <summary>Headline figure of the tests block: what passed, or what failed on a broken build.</summary>
    [ObservableProperty] private string _testsHeadline = string.Empty;
    [ObservableProperty] private string _testsHeadlineSuffix = string.Empty;

    /// <summary>The headline counts failures, so it is painted as such.</summary>
    [ObservableProperty] private bool _headlineIsFailure;

    [ObservableProperty] private bool _showAllPassed;
    [ObservableProperty] private bool _showFailedCount;
    [ObservableProperty] private bool _showSkippedCount;
    [ObservableProperty] private bool _showTestsSeparator;
    [ObservableProperty] private bool _showPassedCount;
    [ObservableProperty] private string _failedCountText = string.Empty;
    [ObservableProperty] private string _skippedCountText = string.Empty;
    [ObservableProperty] private string _passedCountText = string.Empty;

    [ObservableProperty] private bool _hasCoverage;
    [ObservableProperty] private bool _hasLineCoverage;
    [ObservableProperty] private bool _hasBranchCoverage;
    [ObservableProperty] private double _lineCoverage;
    [ObservableProperty] private double _branchCoverage;
    [ObservableProperty] private string _lineCoverageLabel = "—";
    [ObservableProperty] private string _branchCoverageLabel = "—";

    /// <summary>Whole percent shown inside the gauge, where the exact figure would not fit.</summary>
    [ObservableProperty] private string _lineCoverageRounded = string.Empty;
    [ObservableProperty] private string _branchCoverageRounded = string.Empty;

    [ObservableProperty] private string _coverageSource = string.Empty;

    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _hasError;

    public Uri? JobUrl { get; private set; }

    /// <summary>Stands in for the build number in the status panel while no build has finished.</summary>
    private const string NoBuildNumber = "—";

    // Resolved on read, not held in a const: the label has to follow a language change.
    private static string BuildingLabel => Loc.T("Job_Building");
    private static string RequestedLabel => Loc.T("Job_Requested");

    /// <summary>
    /// How long the card keeps claiming a build we asked for and never saw start. A build can sit in
    /// the queue for a while, but past this the card goes back to reporting only what it observes.
    /// </summary>
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Number of the last completed build, as of the latest poll.</summary>
    private int? _lastCompletedNumber;

    /// <summary>What that number was when the user asked for a new build.</summary>
    private int? _numberAtRequest;
    private DateTimeOffset _requestedAt;

    public void Update(MonitoredJobState state)
    {
        ServerName = state.ServerName;
        JobUrl = state.JobUrl;

        var snapshot = state.Snapshot;
        DisplayName = snapshot?.DisplayName is { Length: > 0 } name ? name : JobLabel.Leaf(state.Job.FullName);

        var project = JobLabel.Project(state.Job.FullName);
        ProjectName = project;
        ProjectSeparator = project.Length == 0 ? string.Empty : JobLabel.Chevron;

        FolderPath = state.Ancestors;

        Status = snapshot?.Status ?? BuildStatus.Unknown;
        StatusLabel = StatusMap.Label(Status);
        IsBuilding = snapshot?.IsBuilding ?? false;

        ErrorText = state.Error;
        HasError = !string.IsNullOrEmpty(state.Error);

        var build = snapshot?.LastCompletedBuild;
        _lastCompletedNumber = build?.Number;
        UpdateActivity();

        if (build is null)
        {
            BuildLabel = NoBuildNumber;
            TimingLabel = Loc.T("Job_NoBuildFinished");
            HasTests = false;
            HasCoverage = false;
            HasStats = false;
            MetaLine = ComposeMetaLine();
            return;
        }

        BuildLabel = $"#{build.Number}";
        TimingLabel = $"{Humanize.RelativeTime(build.Timestamp)} · {Humanize.Duration(build.Duration)}";

        UpdateTests(build.Tests);
        UpdateCoverage(state.Coverage);

        HasStats = HasTests || HasCoverage;
        MetaLine = ComposeMetaLine();
    }

    /// <summary>
    /// A build we asked for is invisible for a moment: Jenkins holds it for its quiet period, and
    /// the poller only reports it on a later cycle. Until then the card claims the activity itself,
    /// so the click has an immediate effect — and drops the claim as soon as reality catches up.
    /// </summary>
    private void UpdateActivity()
    {
        if (IsBuildRequested
            && (IsBuilding
                || _lastCompletedNumber != _numberAtRequest
                || DateTimeOffset.Now - _requestedAt > RequestLifetime))
        {
            IsBuildRequested = false;
        }

        IsActive = IsBuilding || IsBuildRequested;
        ActivityLabel = IsBuilding ? BuildingLabel : RequestedLabel;
    }

    private string ComposeMetaLine() =>
        string.Join(" · ", new[] { ServerName, FolderPath, TimingLabel }.Where(part => part.Length > 0));

    /// <summary>
    /// A green build is read by what passed, a broken one by what failed: the headline carries
    /// whichever figure the reader came for, and the line under it says the rest.
    /// </summary>
    private void UpdateTests(TestSummary? tests)
    {
        HasTests = tests is not null && tests.Total > 0;
        if (tests is null)
            return;

        TestsTotal = tests.Total;

        HeadlineIsFailure = Status == BuildStatus.Failure && tests.Failed > 0;
        TestsHeadline = (HeadlineIsFailure ? tests.Failed : tests.Passed).ToString(Loc.FormatCulture);
        TestsHeadlineSuffix = HeadlineIsFailure
            ? $" {Loc.Plural(tests.Failed, "Tests_FailureWordOne", "Tests_FailureWordMany")} / {tests.Total}"
            : $" / {tests.Total}";

        FailedCountText = Loc.Plural(tests.Failed, "Tests_FailedOne", "Tests_FailedMany");
        SkippedCountText = Loc.Plural(tests.Skipped, "Tests_SkippedOne", "Tests_SkippedMany");
        PassedCountText = Loc.Plural(tests.Passed, "Tests_PassedOne", "Tests_PassedMany");

        ShowPassedCount = HeadlineIsFailure;
        ShowAllPassed = !HeadlineIsFailure && tests.Failed == 0 && tests.Skipped == 0;
        ShowFailedCount = !HeadlineIsFailure && tests.Failed > 0;
        ShowSkippedCount = !HeadlineIsFailure && tests.Skipped > 0;
        ShowTestsSeparator = ShowFailedCount && ShowSkippedCount;
    }

    /// <summary>Zero and one both take the singular in French.</summary>
    private void UpdateCoverage(CoverageSummary? coverage)
    {
        HasCoverage = coverage is not null && coverage.HasValue;
        if (coverage is null)
        {
            HasLineCoverage = false;
            HasBranchCoverage = false;
            return;
        }

        HasLineCoverage = coverage.LinePercent.HasValue;
        HasBranchCoverage = coverage.BranchPercent.HasValue;
        LineCoverage = coverage.LinePercent ?? 0;
        BranchCoverage = coverage.BranchPercent ?? 0;
        LineCoverageLabel = Humanize.Percent(coverage.LinePercent);
        BranchCoverageLabel = Humanize.Percent(coverage.BranchPercent);
        LineCoverageRounded = Round(coverage.LinePercent);
        BranchCoverageRounded = Round(coverage.BranchPercent);
        CoverageSource = coverage.Source;
    }

    private static string Round(double? value) =>
        value is null ? string.Empty : Math.Round(value.Value).ToString("0", CultureInfo.CurrentCulture);

    [RelayCommand]
    private void Open() => ShellUtil.OpenUrl(JobUrl);

    /// <summary>
    /// Queues a build of this job. The command is disabled while the request is in flight, so a
    /// double click cannot queue two builds.
    /// </summary>
    [RelayCommand]
    private async Task TriggerBuildAsync()
    {
        var server = _workspace.FindServer(_serverId);
        var job = server?.Jobs.FirstOrDefault(j => j.FullName == FullName);
        if (server is null || job is null)
            return;

        IsTriggering = true;

        try
        {
            var client = _workspace.Monitoring.GetClient(server);
            await client.TriggerBuildAsync(job, CancellationToken.None).ConfigureAwait(true);

            _numberAtRequest = _lastCompletedNumber;
            _requestedAt = DateTimeOffset.Now;
            IsBuildRequested = true;
            UpdateActivity();

            ErrorText = null;
            HasError = false;

            // The build is queued, not started: the poll that follows will not see it yet, but it is
            // what will pick it up the moment it does.
            _workspace.Monitoring.RequestImmediateRefresh();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Starting a build of {FullName}", ex);
            ErrorText = Loc.T("Job_BuildStartFailed", ex.Message);
            HasError = true;
        }
        finally
        {
            IsTriggering = false;
        }
    }

    [RelayCommand]
    private void StopMonitoring()
    {
        var server = _workspace.FindServer(_serverId);
        if (server is null)
            return;

        server.Jobs.RemoveAll(j => j.FullName == FullName);
        _workspace.Save();
    }
}
