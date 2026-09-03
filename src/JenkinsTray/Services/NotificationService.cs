using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.WinUI.Notifications;
using JenkinsTray.Models;

namespace JenkinsTray.Services;

/// <summary>
/// Windows toast notifications for finished builds. Clicking anywhere on the toast raises
/// <see cref="OpenUrlRequested"/> with the URL of the <em>job</em> (not the build), as requested.
/// </summary>
public sealed class NotificationService
{
    private const string ToastGroup = "jenkins-tray";

    private bool _initialized;

    public event Action<string>? OpenUrlRequested;

    public void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;

        try
        {
            // Registers the COM activator + Start-menu shortcut required for a non-packaged app to
            // receive toast activations. Handled entirely by the compat layer.
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;

            // Windows drops toasts whose AUMID has no Start-menu shortcut behind it.
            ToastShortcut.Ensure();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Registering the notification activator", ex);
        }
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var arguments = ToastArguments.Parse(e.Argument);
            if (arguments.TryGetValue("url", out var url) && !string.IsNullOrWhiteSpace(url))
                OpenUrlRequested?.Invoke(url);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // Malformed activation payload — nothing to open.
        }
    }

    public void ShowBuildResult(MonitoredJobState state, BuildInfo build, CoverageSummary? coverage, AppSettings settings)
    {
        var jobUrl = state.JobUrl?.AbsoluteUri;
        if (string.IsNullOrEmpty(jobUrl))
            return;

        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument("action", "openJob")
                .AddArgument("url", jobUrl)
                .AddText($"{StatusMap.Emoji(build.Result)}  {state.QualifiedName} · #{build.Number}")
                .AddText($"{StatusMap.Label(build.Result)} · {Humanize.Duration(build.Duration)}");

            var details = BuildDetailLine(build.Tests, coverage);
            if (details is not null)
                builder.AddText(details);

            // The title already carries the project; the attribution takes the folders above it.
            var ancestors = state.Ancestors;
            builder.AddAttributionText(ancestors.Length == 0
                ? state.ServerName
                : $"{state.ServerName} · {ancestors}");

            var logo = IconRenderer.GetStatusPngPath(build.Result);
            if (logo is not null)
                builder.AddAppLogoOverride(new Uri(logo), ToastGenericAppLogoCrop.Circle);

            builder.AddAudio(
                new Uri(build.Result == BuildStatus.Failure
                    ? "ms-winsoundevent:Notification.Looping.Alarm2"
                    : "ms-winsoundevent:Notification.Default"),
                loop: false,
                silent: !settings.PlaySound);

            var tag = MakeTag(state.Key);
            builder.Show(toast =>
            {
                toast.Tag = tag;
                toast.Group = ToastGroup;
                toast.ExpirationTime = DateTimeOffset.Now.AddHours(6);
            });
        }
        catch (Exception ex)
        {
            // Notifications disabled at OS level, or the COM activator could not register.
            AppLog.Warn("Showing the notification failed", ex);
        }
    }

    /// <summary>Merges tests and coverage into the single body line a toast can comfortably show.</summary>
    private static string? BuildDetailLine(TestSummary? tests, CoverageSummary? coverage)
    {
        var parts = new List<string>(2);

        if (tests is not null && tests.Total > 0)
        {
            var segments = new List<string> { $"{tests.Passed} ✓" };
            if (tests.Failed > 0)
                segments.Add($"{tests.Failed} ✗");
            if (tests.Skipped > 0)
                segments.Add($"{tests.Skipped} ⊘");

            parts.Add(Loc.T("Toast_Tests", string.Join(" · ", segments), tests.Total));
        }

        if (coverage is not null && coverage.HasValue)
        {
            var segments = new List<string>(2);
            if (coverage.LinePercent.HasValue)
                segments.Add(Loc.T("Toast_Lines", Humanize.Percent(coverage.LinePercent)));
            if (coverage.BranchPercent.HasValue)
                segments.Add(Loc.T("Toast_Branches", Humanize.Percent(coverage.BranchPercent)));

            parts.Add(Loc.T("Toast_Coverage", string.Join(" · ", segments)));
        }

        return parts.Count == 0 ? null : string.Join("   |   ", parts);
    }

    /// <summary>Toast tags are limited to 64 chars, so job keys are hashed.</summary>
    private static string MakeTag(string key)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash);
    }

    public void Shutdown()
    {
        if (_initialized)
            ToastNotificationManagerCompat.OnActivated -= OnToastActivated;
    }
}
