namespace JenkinsTray.Models;

/// <summary>Outcome of a build, or the current health of a job.</summary>
public enum BuildStatus
{
    Unknown,
    Success,
    Unstable,
    Failure,
    Aborted,
    NotBuilt,
    Disabled,
}

public static class StatusMap
{
    /// <summary>
    /// Translates a Jenkins job <c>color</c> ("blue", "red_anime", "notbuilt", ...) into a status
    /// plus the "a build is currently running" flag carried by the <c>_anime</c> suffix.
    /// </summary>
    public static (BuildStatus Status, bool Building) FromColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return (BuildStatus.Unknown, false);

        var building = color.EndsWith("_anime", StringComparison.OrdinalIgnoreCase);
        var basePart = building ? color[..^"_anime".Length] : color;

        var status = basePart.ToLowerInvariant() switch
        {
            "blue" or "green" => BuildStatus.Success,
            "yellow" => BuildStatus.Unstable,
            "red" => BuildStatus.Failure,
            "aborted" => BuildStatus.Aborted,
            "notbuilt" or "grey" or "gray" => BuildStatus.NotBuilt,
            "disabled" => BuildStatus.Disabled,
            _ => BuildStatus.Unknown,
        };

        return (status, building);
    }

    public static BuildStatus FromResult(string? result) => result?.ToUpperInvariant() switch
    {
        "SUCCESS" => BuildStatus.Success,
        "UNSTABLE" => BuildStatus.Unstable,
        "FAILURE" => BuildStatus.Failure,
        "ABORTED" => BuildStatus.Aborted,
        "NOT_BUILT" => BuildStatus.NotBuilt,
        _ => BuildStatus.Unknown,
    };

    public static string Label(BuildStatus status) => Services.Loc.T(status switch
    {
        BuildStatus.Success => "Status_Success",
        BuildStatus.Unstable => "Status_Unstable",
        BuildStatus.Failure => "Status_Failure",
        BuildStatus.Aborted => "Status_Aborted",
        BuildStatus.NotBuilt => "Status_NotBuilt",
        BuildStatus.Disabled => "Status_Disabled",
        _ => "Status_Unknown",
    });

    public static string Emoji(BuildStatus status) => status switch
    {
        BuildStatus.Success => "✅",
        BuildStatus.Unstable => "⚠️",
        BuildStatus.Failure => "❌",
        BuildStatus.Aborted => "⛔",
        _ => "❓",
    };

    /// <summary>Higher wins when collapsing several jobs into a single tray icon.</summary>
    public static int Severity(BuildStatus status) => status switch
    {
        BuildStatus.Failure => 5,
        BuildStatus.Unstable => 4,
        BuildStatus.Aborted => 3,
        BuildStatus.Unknown => 2,
        BuildStatus.NotBuilt => 1,
        BuildStatus.Disabled => 0,
        BuildStatus.Success => 0,
        _ => 0,
    };
}
