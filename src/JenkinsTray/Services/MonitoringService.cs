using JenkinsTray.Models;

namespace JenkinsTray.Services;

/// <summary>Everything the UI needs to know about one monitored job at a given moment.</summary>
public sealed class MonitoredJobState
{
    public required string Key { get; init; }
    public required string ServerId { get; init; }
    public required string ServerName { get; set; }
    public required MonitoredJob Job { get; set; }

    public Uri? JobUrl { get; set; }
    public JobSnapshot? Snapshot { get; set; }
    public CoverageSummary? Coverage { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// The job named so it can be told apart: <c>project › branch</c>. The leaf alone is ambiguous on
    /// a multibranch pipeline, where every project has a <c>master</c>.
    /// </summary>
    public string QualifiedName => JobLabel.Qualified(Job.FullName, Snapshot?.DisplayName ?? Job.DisplayName);

    /// <summary>Folders above the project — the part of the ancestry the qualified name leaves out.</summary>
    public string Ancestors => JobLabel.Ancestors(Job.FullName);

    /// <summary>Build number of the last completed build we have already accounted for.</summary>
    public int? LastSeenBuildNumber { get; set; }

    public BuildStatus LastResult { get; set; } = BuildStatus.Unknown;
    public static string MakeKey(string serverId, string fullName) => serverId + "::" + fullName;
}

public sealed class BuildCompletedEventArgs(MonitoredJobState state, BuildInfo build, CoverageSummary? coverage) : EventArgs
{
    public MonitoredJobState State { get; } = state;
    public BuildInfo Build { get; } = build;
    public CoverageSummary? Coverage { get; } = coverage;
}

/// <summary>
/// Background poller. Owns the per-job state, detects newly completed builds and enriches them with
/// test and coverage data before announcing them. Raises events on a thread-pool thread — callers
/// are responsible for marshalling to the UI.
/// </summary>
public sealed class MonitoringService : IDisposable
{
    private const int MaxParallelPolls = 4;

    private readonly JenkinsClientPool _pool = new();
    private readonly Dictionary<string, MonitoredJobState> _states = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private AppSettings _settings = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private TaskCompletionSource? _wakeUp;

    public event EventHandler<BuildCompletedEventArgs>? BuildCompleted;

    /// <summary>Raised whenever <see cref="States"/> may have changed.</summary>
    public event EventHandler? StatesChanged;

    public DateTimeOffset? LastRefresh { get; private set; }

    public bool IsRefreshing { get; private set; }

    public IReadOnlyList<MonitoredJobState> States
    {
        get
        {
            lock (_gate)
                return [.. _states.Values
                    .OrderBy(s => s.ServerName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(s => s.Job.FullName, StringComparer.CurrentCultureIgnoreCase)];
        }
    }

    /// <summary>Worst status across all monitored jobs — drives the tray icon.</summary>
    public BuildStatus AggregateStatus
    {
        get
        {
            var states = States;
            if (states.Count == 0)
                return BuildStatus.Unknown;

            return states
                .Select(s => s.Snapshot?.Status ?? BuildStatus.Unknown)
                .OrderByDescending(StatusMap.Severity)
                .First();
        }
    }

    public bool AnyBuilding => States.Any(s => s.Snapshot?.IsBuilding == true);

    // ---------------------------------------------------------------- lifecycle

    public void Start()
    {
        if (_loop is not null)
            return;

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>
    /// Re-reads the configuration: adds newly checked jobs, drops unchecked ones, and keeps the
    /// already-known build numbers of the survivors so no spurious notification is fired.
    /// </summary>
    public void ApplyConfiguration(AppSettings settings)
    {
        _settings = settings.Clone();

        lock (_gate)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var server in _settings.Servers)
            {
                foreach (var job in server.Jobs)
                {
                    var key = MonitoredJobState.MakeKey(server.Id, job.FullName);
                    seen.Add(key);

                    if (_states.TryGetValue(key, out var state))
                    {
                        state.ServerName = server.Name;
                        state.Job = job;
                    }
                    else
                    {
                        _states[key] = new MonitoredJobState
                        {
                            Key = key,
                            ServerId = server.Id,
                            ServerName = server.Name,
                            Job = job,
                        };
                    }
                }
            }

            foreach (var key in _states.Keys.Where(k => !seen.Contains(k)).ToList())
                _states.Remove(key);
        }

        _pool.Prune(_settings.Servers.Select(s => s.Id));
        StatesChanged?.Invoke(this, EventArgs.Empty);
        RequestImmediateRefresh();
    }

    public void RequestImmediateRefresh() => Interlocked.Exchange(ref _wakeUp, null)?.TrySetResult();

    public async Task RefreshNowAsync(CancellationToken ct = default)
    {
        await RefreshAsync(ct).ConfigureAwait(false);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A whole failed cycle must never kill the loop; per-job errors are already captured.
            }

            var interval = TimeSpan.FromSeconds(Math.Clamp(_settings.PollIntervalSeconds, 5, 3600));
            var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _wakeUp, wake);

            try
            {
                await Task.WhenAny(Task.Delay(interval, ct), wake.Task).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // ---------------------------------------------------------------- polling

    private async Task RefreshAsync(CancellationToken ct)
    {
        if (!await _refreshLock.WaitAsync(0, ct).ConfigureAwait(false))
            return;

        try
        {
            IsRefreshing = true;
            StatesChanged?.Invoke(this, EventArgs.Empty);

            var settings = _settings;
            var work = new List<(ServerConfig Server, MonitoredJobState State)>();

            lock (_gate)
            {
                foreach (var server in settings.Servers.Where(s => s.Enabled))
                    foreach (var job in server.Jobs)
                        if (_states.TryGetValue(MonitoredJobState.MakeKey(server.Id, job.FullName), out var state))
                            work.Add((server, state));
            }

            var completions = new List<BuildCompletedEventArgs>();
            var completionsGate = new Lock();

            await Parallel.ForEachAsync(
                work,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelPolls, CancellationToken = ct },
                async (item, token) =>
                {
                    var completion = await PollOneAsync(item.Server, item.State, settings, token).ConfigureAwait(false);
                    if (completion is not null)
                        lock (completionsGate)
                            completions.Add(completion);
                }).ConfigureAwait(false);

            LastRefresh = DateTimeOffset.Now;

            foreach (var completion in completions.OrderBy(c => c.Build.Timestamp))
                BuildCompleted?.Invoke(this, completion);
        }
        finally
        {
            IsRefreshing = false;
            _refreshLock.Release();
            StatesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<BuildCompletedEventArgs?> PollOneAsync(
        ServerConfig server, MonitoredJobState state, AppSettings settings, CancellationToken ct)
    {
        try
        {
            var client = _pool.Get(server);
            state.JobUrl = client.Absolute(state.Job.Path);

            var snapshot = await client.GetJobSnapshotAsync(state.Job, ct).ConfigureAwait(false);
            state.Snapshot = snapshot;
            state.Error = null;

            var build = snapshot.LastCompletedBuild;
            if (build is null)
            {
                state.Coverage = null;
                return null;
            }

            var isFirstSighting = state.LastSeenBuildNumber is null;
            var isNewBuild = state.LastSeenBuildNumber is int known && build.Number != known;

            if (isFirstSighting || isNewBuild)
            {
                // Only pay for the extra round-trips when the build we are looking at is new to us.
                if (build.Tests is null)
                    build = build with { Tests = await client.GetTestReportAsync(build.Path, ct).ConfigureAwait(false) };

                state.Coverage = await client.GetCoverageAsync(build.Path, ct).ConfigureAwait(false);
                state.Snapshot = snapshot with { LastCompletedBuild = build };
            }

            var previousResult = state.LastResult;
            state.LastSeenBuildNumber = build.Number;
            state.LastResult = build.Result;

            if (!isNewBuild)
                return null;

            if (!settings.ShouldNotify(build.Result))
                return null;

            if (settings.NotifyOnlyOnStatusChange && previousResult == build.Result)
                return null;

            return new BuildCompletedEventArgs(state, build, state.Coverage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            return null;
        }
    }

    // ---------------------------------------------------------------- discovery passthrough

    public JenkinsClient GetClient(ServerConfig server) => _pool.Get(server);

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
            // Shutting down anyway.
        }

        _cts?.Dispose();
        _refreshLock.Dispose();
        _pool.Dispose();
    }
}
