namespace JenkinsTray.Models;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Interface language. <see cref="System"/> takes French on a French Windows and English
/// everywhere else — English being the language every key is guaranteed to have.
/// </summary>
public enum AppLanguage
{
    System,
    English,
    French,
}

/// <summary>How the dashboard cards are ordered.</summary>
public enum DashboardSort
{
    ServerThenName,
    Name,
    Status,
    LastBuild,
    Duration,
    Coverage,
}

/// <summary>Everything persisted to <c>%APPDATA%\JenkinsTray\settings.json</c>.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public List<ServerConfig> Servers { get; set; } = [];

    /// <summary>How often monitored jobs are polled, in seconds.</summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>How deep the job explorer walks into folders / multibranch projects.</summary>
    public int DiscoveryDepth { get; set; } = 6;

    /// <summary>Off by default so a fresh install shows its window instead of vanishing into the tray.</summary>
    public bool StartMinimized { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool StartWithWindows { get; set; }

    public bool NotifyOnSuccess { get; set; } = true;
    public bool NotifyOnUnstable { get; set; } = true;
    public bool NotifyOnFailure { get; set; } = true;
    public bool NotifyOnAborted { get; set; }

    /// <summary>When true, a build only notifies if its result differs from the previous build.</summary>
    public bool NotifyOnlyOnStatusChange { get; set; }

    public bool PlaySound { get; set; } = true;

    public AppTheme Theme { get; set; } = AppTheme.System;

    public AppLanguage Language { get; set; } = AppLanguage.System;

    public DashboardSort DashboardSort { get; set; } = DashboardSort.ServerThenName;

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, SettingsJson.Options);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, SettingsJson.Options) ?? new AppSettings();
    }

    public bool ShouldNotify(BuildStatus status) => status switch
    {
        BuildStatus.Success => NotifyOnSuccess,
        BuildStatus.Unstable => NotifyOnUnstable,
        BuildStatus.Failure => NotifyOnFailure,
        BuildStatus.Aborted => NotifyOnAborted,
        _ => false,
    };
}

public sealed class ServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;

    /// <summary>Root URL of the Jenkins instance, e.g. <c>https://ci.example.com/</c>.</summary>
    public string Url { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    /// <summary>API token, DPAPI-encrypted for the current Windows user. Never stored in clear text.</summary>
    public string TokenProtected { get; set; } = string.Empty;

    public bool AcceptInvalidCertificate { get; set; }
    public bool Enabled { get; set; } = true;

    public List<MonitoredJob> Jobs { get; set; } = [];

    /// <summary>One-line description for the settings list; skips the parts that are empty.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary => string.Join(
        " · ",
        new[] { Url, Username, Services.Loc.T("Settings_ServerSummaryJobs", Jobs.Count) }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    public ServerConfig Clone() => new()
    {
        Id = Id,
        Name = Name,
        Url = Url,
        Username = Username,
        TokenProtected = TokenProtected,
        AcceptInvalidCertificate = AcceptInvalidCertificate,
        Enabled = Enabled,
        Jobs = [.. Jobs.Select(j => new MonitoredJob { FullName = j.FullName, DisplayName = j.DisplayName, Path = j.Path })],
    };

    /// <summary>Changes to these fields invalidate a cached <see cref="Services.JenkinsClient"/>.</summary>
    public string ConnectionFingerprint() => string.Join('|', Url, Username, TokenProtected, AcceptInvalidCertificate);
}

public sealed class MonitoredJob
{
    /// <summary>Jenkins <c>fullName</c>, e.g. <c>my-folder/my-app/main</c>. Stable identity of the job.</summary>
    public string FullName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Path relative to the server root, already URL-encoded by Jenkins,
    /// e.g. <c>job/my-folder/job/my-app/job/feature%2Fx/</c>.
    /// </summary>
    public string Path { get; set; } = string.Empty;
}

internal static class SettingsJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
