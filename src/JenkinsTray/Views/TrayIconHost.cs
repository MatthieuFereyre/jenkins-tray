using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using JenkinsTray.Models;
using JenkinsTray.Services;

namespace JenkinsTray.Views;

/// <summary>
/// Owns the notification-area icon: its colour reflects the worst status across monitored jobs,
/// it pulses while something is building, and its menu lists the watched jobs.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly Workspace _workspace;
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _jobsMenu;
    private readonly DispatcherTimer _pulse;

    private bool _pulseDim;
    private BuildStatus? _appliedStatus;
    private bool _appliedDim;

    public TrayIconHost(Workspace workspace)
    {
        _workspace = workspace;

        _jobsMenu = new MenuItem { Header = Loc.T("Tray_MonitoredJobs") };

        _icon = new TaskbarIcon
        {
            ToolTipText = "Jenkins Tray",
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
            ContextMenu = BuildMenu(),
            Icon = IconRenderer.CreateTrayIcon(BuildStatus.Unknown),
        };

        _icon.TrayLeftMouseUp += (_, _) => ShowRequested?.Invoke();
        _icon.ForceCreate();

        _pulse = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(650) };
        _pulse.Tick += (_, _) =>
        {
            _pulseDim = !_pulseDim;
            ApplyIcon();
        };

        // The menu is built once, so its fixed entries would keep the language they were created
        // in. Rebuilding it is cheaper than making each header a binding on a menu nothing else
        // ever changes.
        Loc.LanguageChanged += OnLanguageChanged;

        Update();
    }

    private void OnLanguageChanged()
    {
        _jobsMenu.Header = Loc.T("Tray_MonitoredJobs");

        // The submenu instance is reused by the rebuilt menu, and WPF refuses an element that is
        // still the logical child of another one: the previous menu has to let go of it first.
        _icon.ContextMenu?.Items.Clear();

        _icon.ContextMenu = BuildMenu();
        Update();
    }

    public event Action? ShowRequested;
    public event Action<int>? NavigateRequested;
    public event Action? ExitRequested;
    public event Action? RefreshRequested;

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(_jobsMenu);
        menu.Items.Add(new Separator());

        var open = new MenuItem { Header = Loc.T("Tray_Open"), FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => ShowRequested?.Invoke();
        menu.Items.Add(open);

        var refresh = new MenuItem { Header = Loc.T("Tray_RefreshNow") };
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        menu.Items.Add(refresh);

        var jobs = new MenuItem { Header = Loc.T("Tray_ChooseJobs") };
        jobs.Click += (_, _) => NavigateRequested?.Invoke(1);
        menu.Items.Add(jobs);

        var settings = new MenuItem { Header = Loc.T("Tray_Settings") };
        settings.Click += (_, _) => NavigateRequested?.Invoke(2);
        menu.Items.Add(settings);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = Loc.T("Tray_Exit") };
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exit);

        return menu;
    }

    /// <summary>Refreshes icon, tooltip and job submenu. Must be called on the UI thread.</summary>
    public void Update()
    {
        var states = _workspace.Monitoring.States;
        var building = _workspace.Monitoring.AnyBuilding;

        if (building && !_pulse.IsEnabled)
        {
            _pulse.Start();
        }
        else if (!building && _pulse.IsEnabled)
        {
            _pulse.Stop();
            _pulseDim = false;
        }

        ApplyIcon();
        _icon.ToolTipText = BuildTooltip(states);
        RebuildJobsMenu(states);
    }

    /// <summary>
    /// Assigning <see cref="TaskbarIcon.Icon"/> hands ownership over: the setter disposes the value
    /// it replaces. So every assignment must use a brand new instance, and we only assign when the
    /// glyph actually changed — re-assigning on every refresh would be pure churn.
    /// </summary>
    private void ApplyIcon()
    {
        var status = _workspace.Monitoring.AggregateStatus;

        if (_appliedStatus == status && _appliedDim == _pulseDim)
            return;

        _appliedStatus = status;
        _appliedDim = _pulseDim;

        try
        {
            _icon.Icon = IconRenderer.CreateTrayIcon(status, _pulseDim ? 0.4 : 1.0);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or ExternalException)
        {
            // A cosmetic glitch on the tray glyph must never surface as an error dialog.
            AppLog.Warn("Updating the notification area icon", ex);
        }
    }

    private string BuildTooltip(IReadOnlyList<MonitoredJobState> states)
    {
        if (states.Count == 0)
            return Loc.T("Tray_TooltipNoJob");

        var failures = states.Count(s => s.Snapshot?.Status == BuildStatus.Failure);
        var unstable = states.Count(s => s.Snapshot?.Status == BuildStatus.Unstable);
        var building = states.Count(s => s.Snapshot?.IsBuilding == true);

        var parts = new List<string> { $"{states.Count} job(s)" };

        if (failures > 0)
            parts.Add(Loc.T("Dashboard_FailingCount", failures));
        if (unstable > 0)
            parts.Add(Loc.T("Tray_UnstableCount", unstable));
        if (building > 0)
            parts.Add(Loc.T("Dashboard_BuildingCount", building));
        if (failures == 0 && unstable == 0)
            parts.Add(Loc.T("Tray_AllGreen"));

        var text = "Jenkins Tray — " + string.Join(", ", parts);

        // The Win32 tooltip is capped at 127 characters.
        return text.Length <= 127 ? text : text[..127];
    }

    private void RebuildJobsMenu(IReadOnlyList<MonitoredJobState> states)
    {
        _jobsMenu.Items.Clear();

        if (states.Count == 0)
        {
            _jobsMenu.Items.Add(new MenuItem { Header = Loc.T("Tray_NoJobMonitored"), IsEnabled = false });
            return;
        }

        foreach (var state in states)
        {
            var status = state.Snapshot?.Status ?? BuildStatus.Unknown;
            var build = state.Snapshot?.LastCompletedBuild;

            var label = build is null
                ? $"{StatusMap.Emoji(status)}  {state.QualifiedName}"
                : $"{StatusMap.Emoji(status)}  {state.QualifiedName} · #{build.Number}";

            var item = new MenuItem
            {
                Header = label,
                ToolTip = $"{state.ServerName} — {state.Job.FullName}",
            };

            var url = state.JobUrl;
            item.Click += (_, _) => ShellUtil.OpenUrl(url);

            _jobsMenu.Items.Add(item);
        }
    }

    public void Dispose()
    {
        // A static event keeps whatever subscribes to it alive: without this the host outlives its
        // own disposal, and a replacement would find two of them reacting.
        Loc.LanguageChanged -= OnLanguageChanged;

        _pulse.Stop();
        _icon.Dispose();
    }
}
