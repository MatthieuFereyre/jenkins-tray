using JenkinsTray.Models;

namespace JenkinsTray.Services;

/// <summary>
/// Single owner of the mutable application state: the settings object everyone edits, the poller,
/// and the notifier. Any change goes through <see cref="Save"/> so persistence and the monitoring
/// loop never drift apart.
/// </summary>
public sealed class Workspace : IDisposable
{
    private readonly SettingsStore _store = new();

    public Workspace()
    {
        Settings = _store.Load();

        // The registry is the source of truth for the startup entry.
        Settings.StartWithWindows = StartupManager.IsEnabled();
    }

    public AppSettings Settings { get; }

    public MonitoringService Monitoring { get; } = new();

    public NotificationService Notifications { get; } = new();

    public string SettingsFilePath => _store.FilePath;

    /// <summary>Raised after the settings have been persisted and pushed to the poller.</summary>
    public event EventHandler? SettingsChanged;

    public void Save()
    {
        _store.Save(Settings);
        Monitoring.ApplyConfiguration(Settings);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Persists a preference the poller has no opinion about — a display option. Skips the
    /// re-configuration and the immediate poll that <see cref="Save"/> triggers.
    /// </summary>
    public void SavePreferences() => _store.Save(Settings, rotateBackups: false);

    public ServerConfig? FindServer(string id) => Settings.Servers.FirstOrDefault(s => s.Id == id);

    public bool IsMonitored(ServerConfig server, string fullName) =>
        server.Jobs.Any(j => j.FullName == fullName);

    public void SetMonitored(ServerConfig server, MonitoredJob job, bool monitored)
    {
        if (Apply(server, job, monitored))
            Save();
    }

    /// <summary>Ticking a folder moves many jobs at once: one write, one poll, one event.</summary>
    public void SetMonitored(ServerConfig server, IEnumerable<MonitoredJob> jobs, bool monitored)
    {
        var changed = false;

        foreach (var job in jobs)
            changed |= Apply(server, job, monitored);

        if (changed)
            Save();
    }

    private static bool Apply(ServerConfig server, MonitoredJob job, bool monitored)
    {
        var index = server.Jobs.FindIndex(j => j.FullName == job.FullName);

        if (monitored && index < 0)
            server.Jobs.Add(job);
        else if (!monitored && index >= 0)
            server.Jobs.RemoveAt(index);
        else
            return false;

        return true;
    }

    public void Dispose()
    {
        Monitoring.Dispose();
        Notifications.Shutdown();
    }
}
