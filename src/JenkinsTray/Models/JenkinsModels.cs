namespace JenkinsTray.Models;

/// <summary>A node of the job tree returned by the job explorer: either a folder or a buildable job.</summary>
public sealed class JobNode
{
    public required string Name { get; init; }
    public required string FullName { get; init; }

    /// <summary>Path relative to the server root, ending with a slash.</summary>
    public required string Path { get; init; }

    public string? Color { get; init; }
    public bool IsFolder { get; init; }
    public bool Buildable { get; init; }

    public List<JobNode> Children { get; } = [];
}

public sealed record TestSummary(int Total, int Failed, int Skipped)
{
    public int Passed => Math.Max(0, Total - Failed - Skipped);
}

public sealed record CoverageSummary(double? LinePercent, double? BranchPercent, string Source)
{
    public bool HasValue => LinePercent.HasValue || BranchPercent.HasValue;
}

public sealed record BuildInfo(
    int Number,
    string DisplayName,
    string Path,
    BuildStatus Result,
    DateTimeOffset Timestamp,
    TimeSpan Duration,
    TestSummary? Tests);

/// <summary>One poll of a monitored job.</summary>
public sealed record JobSnapshot(
    string FullName,
    string DisplayName,
    string Path,
    BuildStatus Status,
    bool IsBuilding,
    BuildInfo? LastCompletedBuild);
