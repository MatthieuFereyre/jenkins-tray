using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JenkinsTray.Models;
using JenkinsTray.Services;

namespace JenkinsTray.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly Workspace _workspace;
    private readonly IDialogService _dialogs;
    private bool _loading;

    public SettingsViewModel(Workspace workspace, IDialogService dialogs)
    {
        _workspace = workspace;
        _dialogs = dialogs;
        Reload();
    }

    public ObservableCollection<ServerConfig> Servers { get; } = [];

    [ObservableProperty] private ServerConfig? _selectedServer;
    [ObservableProperty] private bool _hasServers;

    // Doubles because ui:NumberBox binds a double? — the clamp back to int happens on persist.
    [ObservableProperty] private double _pollIntervalSeconds = 30;
    [ObservableProperty] private double _discoveryDepth = 6;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _minimizeToTrayOnClose;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _notifyOnSuccess;
    [ObservableProperty] private bool _notifyOnUnstable;
    [ObservableProperty] private bool _notifyOnFailure;
    [ObservableProperty] private bool _notifyOnAborted;
    [ObservableProperty] private bool _notifyOnlyOnStatusChange;
    [ObservableProperty] private bool _playSound;

    /// <summary>0 = system, 1 = light, 2 = dark.</summary>
    [ObservableProperty] private int _themeIndex;

    /// <summary>0 = system, 1 = English, 2 = French.</summary>
    [ObservableProperty] private int _languageIndex;

    public string SettingsFilePath => _workspace.SettingsFilePath;

    public void Reload()
    {
        _loading = true;

        var settings = _workspace.Settings;

        Servers.Clear();
        foreach (var server in settings.Servers)
            Servers.Add(server);

        HasServers = Servers.Count > 0;
        SelectedServer ??= Servers.FirstOrDefault();

        PollIntervalSeconds = settings.PollIntervalSeconds;
        DiscoveryDepth = settings.DiscoveryDepth;
        StartMinimized = settings.StartMinimized;
        MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
        StartWithWindows = settings.StartWithWindows;
        NotifyOnSuccess = settings.NotifyOnSuccess;
        NotifyOnUnstable = settings.NotifyOnUnstable;
        NotifyOnFailure = settings.NotifyOnFailure;
        NotifyOnAborted = settings.NotifyOnAborted;
        NotifyOnlyOnStatusChange = settings.NotifyOnlyOnStatusChange;
        PlaySound = settings.PlaySound;
        ThemeIndex = (int)settings.Theme;
        LanguageIndex = (int)settings.Language;

        _loading = false;
    }

    private void Persist(Action<AppSettings> mutate)
    {
        if (_loading)
            return;

        mutate(_workspace.Settings);
        _workspace.Save();
    }

    partial void OnPollIntervalSecondsChanged(double value) =>
        Persist(s => s.PollIntervalSeconds = Math.Clamp((int)value, 5, 3600));

    partial void OnDiscoveryDepthChanged(double value) =>
        Persist(s => s.DiscoveryDepth = Math.Clamp((int)value, 1, 10));

    partial void OnStartMinimizedChanged(bool value) => Persist(s => s.StartMinimized = value);

    partial void OnMinimizeToTrayOnCloseChanged(bool value) => Persist(s => s.MinimizeToTrayOnClose = value);

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading)
            return;

        StartupManager.SetEnabled(value);
        Persist(s => s.StartWithWindows = StartupManager.IsEnabled());
    }

    partial void OnNotifyOnSuccessChanged(bool value) => Persist(s => s.NotifyOnSuccess = value);

    partial void OnNotifyOnUnstableChanged(bool value) => Persist(s => s.NotifyOnUnstable = value);

    partial void OnNotifyOnFailureChanged(bool value) => Persist(s => s.NotifyOnFailure = value);

    partial void OnNotifyOnAbortedChanged(bool value) => Persist(s => s.NotifyOnAborted = value);

    partial void OnNotifyOnlyOnStatusChangeChanged(bool value) => Persist(s => s.NotifyOnlyOnStatusChange = value);

    partial void OnPlaySoundChanged(bool value) => Persist(s => s.PlaySound = value);

    partial void OnThemeIndexChanged(int value)
    {
        if (_loading)
            return;

        var theme = (AppTheme)Math.Clamp(value, 0, 2);
        ThemeService.Apply(theme);
        Persist(s => s.Theme = theme);
    }

    partial void OnLanguageIndexChanged(int value)
    {
        if (_loading)
            return;

        var language = (AppLanguage)Math.Clamp(value, 0, 2);
        Loc.Apply(language);
        Persist(s => s.Language = language);

        // ServerConfig.Summary is a plain computed property on a model that raises nothing, so the
        // rows keep the sentence they were built with until the list is filled again.
        Reload();
    }

    [RelayCommand]
    private void AddServer()
    {
        var config = new ServerConfig();

        if (!_dialogs.EditServer(config, isNew: true))
            return;

        _workspace.Settings.Servers.Add(config);
        _workspace.Save();
        Reload();
        SelectedServer = _workspace.Settings.Servers.LastOrDefault();
    }

    [RelayCommand]
    private void EditServer(ServerConfig? server)
    {
        server ??= SelectedServer;
        if (server is null)
            return;

        if (!_dialogs.EditServer(server, isNew: false))
            return;

        _workspace.Save();
        Reload();
    }

    [RelayCommand]
    private async Task DeleteServerAsync(ServerConfig? server)
    {
        server ??= SelectedServer;
        if (server is null)
            return;

        var monitored = server.Jobs.Count;
        var detail = monitored == 0
            ? string.Empty
            : Loc.Plural(monitored, "Settings_DeleteServerDetailOne", "Settings_DeleteServerDetailMany");

        if (!await _dialogs.ConfirmAsync(
                Loc.T("Settings_DeleteServerTitle"),
                Loc.T("Settings_DeleteServerQuestion", server.Name, detail),
                Loc.T("Settings_Delete")))
            return;

        _workspace.Settings.Servers.Remove(server);
        _workspace.Save();
        SelectedServer = null;
        Reload();
    }

    [RelayCommand]
    private void OpenSettingsFolder() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = SettingsStore.DataDirectory,
            UseShellExecute = true,
        });
}
