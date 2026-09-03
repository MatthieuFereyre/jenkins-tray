using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using JenkinsTray.Services;
using JenkinsTray.ViewModels;
using JenkinsTray.Views;

namespace JenkinsTray;

public partial class App : Application
{
    // Keyed on the data directory so a throwaway profile runs beside the real one instead of
    // being folded into it by the single-instance check.
    private static string InstanceKey => Convert.ToHexString(
        MD5.HashData(Encoding.UTF8.GetBytes(SettingsStore.DataDirectory.ToLowerInvariant())))[..16];

    private static string MutexName => $@"Local\JenkinsTray.SingleInstance.{InstanceKey}";
    private static string ShowEventName => $@"Local\JenkinsTray.ShowWindow.{InstanceKey}";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private Workspace? _workspace;
    private TrayIconHost? _tray;
    private ShellWindow? _shell;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The window can be hidden to the tray, so the app must never exit just because it closed.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Must happen before anything touches the data directory.
        ApplyDataDirectoryOverride(e.Args);

        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            SignalRunningInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        _workspace = new Workspace();

        // Before anything builds a string: the first window, the tray menu and the settings page
        // all read their labels while they are constructed.
        Loc.Apply(_workspace.Settings.Language);

        ThemeService.Apply(_workspace.Settings.Theme);

        _workspace.Notifications.Initialize();
        _workspace.Notifications.OpenUrlRequested += url =>
            Dispatcher.BeginInvoke(() => ShellUtil.OpenUrl(url));

        _workspace.Monitoring.BuildCompleted += OnBuildCompleted;
        _workspace.Monitoring.StatesChanged += (_, _) => Dispatcher.BeginInvoke(() => _tray?.Update());

        var dialogs = new DialogService();
        _shell = new ShellWindow(new ShellViewModel(_workspace, dialogs), _workspace);
        dialogs.Owner = _shell;

        _tray = new TrayIconHost(_workspace);
        _tray.ShowRequested += () => _shell.ShowAndActivate();
        _tray.NavigateRequested += index => _shell.ShowAndActivate(index);
        _tray.RefreshRequested += () => _ = _workspace.Monitoring.RefreshNowAsync();
        _tray.ExitRequested += ExitApplication;

        StartShowListener();

        _workspace.Monitoring.ApplyConfiguration(_workspace.Settings);
        _workspace.Monitoring.Start();

        // A process launched by a toast click should just open the job, not pop its window open.
        var startHidden = _workspace.Settings.StartMinimized
            || e.Args.Contains("--minimized")
            || CommunityToolkit.WinUI.Notifications.ToastNotificationManagerCompat.WasCurrentProcessToastActivated();

        if (!startHidden)
            _shell.Show();
    }

    private void OnBuildCompleted(object? sender, BuildCompletedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_workspace is not null)
                _workspace.Notifications.ShowBuildResult(e.State, e.Build, e.Coverage, _workspace.Settings);
        });
    }

    // ---------------------------------------------------------------- data directory

    /// <summary>
    /// Honours <c>--data-dir &lt;path&gt;</c> and the <c>JENKINSTRAY_DATA_DIR</c> variable, so a
    /// throwaway instance can run without ever writing over the real configuration.
    /// </summary>
    private static void ApplyDataDirectoryOverride(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
            {
                SettingsStore.UseDataDirectory(args[i]["--data-dir=".Length..].Trim('"'));
                return;
            }

            if (args[i].Equals("--data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                SettingsStore.UseDataDirectory(args[i + 1].Trim('"'));
                return;
            }
        }

        SettingsStore.UseDataDirectory(Environment.GetEnvironmentVariable("JENKINSTRAY_DATA_DIR") ?? string.Empty);
    }

    // ---------------------------------------------------------------- single instance

    private static void SignalRunningInstance()
    {
        try
        {
            EventWaitHandle.OpenExisting(ShowEventName).Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other instance is still starting up; nothing useful to do.
        }
    }

    private void StartShowListener()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => _shell?.ShowAndActivate());
        })
        {
            IsBackground = true,
            Name = "JenkinsTray.ShowListener",
        };

        listener.Start();
    }

    // ---------------------------------------------------------------- shutdown

    private void ExitApplication()
    {
        if (_shell is not null)
        {
            _shell.AllowClose = true;
            _shell.Close();
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _workspace?.Dispose();
        _showEvent?.Dispose();

        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owner (second instance) — nothing to release.
            }

            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            File.AppendAllText(
                Path.Combine(SettingsStore.DataDirectory, "error.log"),
                $"{DateTimeOffset.Now:u} {e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging is best-effort.
        }

        MessageBox.Show(
            $"Une erreur inattendue est survenue :\n\n{e.Exception.Message}",
            "Jenkins Tray",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
