using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JenkinsTray.Models;

namespace JenkinsTray.Services;

/// <summary>
/// Client for one Jenkins instance. Reading goes through the remote access API
/// (<c>.../api/json</c>) with <c>tree=</c> filters so we transfer as little as possible; the only
/// thing it writes is a build request.
/// </summary>
public sealed class JenkinsClient : IDisposable
{
    private const string JobFields = "name,fullName,url,color,buildable,_class";

    private static readonly string[] ContainerClassMarkers =
    [
        "folder", "multibranch", "organizationfolder", "cloudbees.hudson.plugins.folder",
    ];

    private readonly HttpClient _http;

    /// <summary>Last CSRF crumb obtained from the instance, if it asks for one.</summary>
    private volatile Crumb? _crumb;

    public JenkinsClient(ServerConfig config)
    {
        BaseUri = NormalizeBaseUri(config.Url);
        Fingerprint = config.ConnectionFingerprint();

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        if (config.AcceptInvalidCertificate)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        _http = new HttpClient(handler)
        {
            BaseAddress = BaseUri,
            Timeout = TimeSpan.FromSeconds(30),
        };

        var token = SecretProtector.Unprotect(config.TokenProtected);
        if (!string.IsNullOrWhiteSpace(config.Username) && !string.IsNullOrEmpty(token))
        {
            var raw = Encoding.UTF8.GetBytes($"{config.Username}:{token}");
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        }

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("JenkinsTray/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public Uri BaseUri { get; }

    public string Fingerprint { get; }

    public static Uri NormalizeBaseUri(string url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            throw new UriFormatException("L'URL du serveur est vide.");

        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "https://" + trimmed;

        if (!trimmed.EndsWith('/'))
            trimmed += "/";

        return new Uri(trimmed, UriKind.Absolute);
    }

    /// <summary>Absolute URL for a path relative to the server root — what we hand to the browser.</summary>
    public Uri Absolute(string relativePath) => new(BaseUri, relativePath);

    // ---------------------------------------------------------------- connection

    /// <summary>Verifies credentials and returns a human-readable description of the instance.</summary>
    public async Task<string> TestConnectionAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/json?tree=mode,nodeName,numExecutors", ct).ConfigureAwait(false);
        EnsureSuccess(response);

        var version = response.Headers.TryGetValues("X-Jenkins", out var values)
            ? values.FirstOrDefault()
            : null;

        var identity = await TryGetCurrentUserAsync(ct).ConfigureAwait(false);

        var parts = new List<string>();
        parts.Add(version is null ? Loc.T("Jenkins_Reachable") : $"Jenkins {version}");
        parts.Add(identity is null
            ? Loc.T("Jenkins_AnonymousUser")
            : Loc.T("Jenkins_SignedInAs", identity));
        return string.Join(" — ", parts);
    }

    private async Task<string?> TryGetCurrentUserAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync("me/api/json?tree=id,fullName", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var name = GetString(doc.RootElement, "fullName") ?? GetString(doc.RootElement, "id");
            return string.Equals(name, "anonymous", StringComparison.OrdinalIgnoreCase) ? null : name;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- discovery

    /// <summary>
    /// Walks the whole instance in a single request, descending <paramref name="depth"/> levels into
    /// folders / multibranch projects. Returns a synthetic root whose children are the top-level items.
    /// </summary>
    public async Task<JobNode> GetJobTreeAsync(int depth, CancellationToken ct)
    {
        var tree = BuildTreeExpression(Math.Clamp(depth, 1, 10));

        using var response = await _http.GetAsync($"api/json?tree={tree}", ct).ConfigureAwait(false);
        EnsureSuccess(response);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        var root = new JobNode { Name = "/", FullName = string.Empty, Path = string.Empty, IsFolder = true };
        if (doc.RootElement.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
            foreach (var job in jobs.EnumerateArray())
                root.Children.Add(ReadJobNode(job));

        return root;
    }

    private static string BuildTreeExpression(int depth)
    {
        var inner = JobFields;
        for (var i = 0; i < depth; i++)
            inner = $"{JobFields},jobs[{inner}]";

        return $"jobs[{inner}]";
    }

    private JobNode ReadJobNode(JsonElement element)
    {
        var name = GetString(element, "name") ?? "(sans nom)";
        var fullName = GetString(element, "fullName") ?? name;
        var url = GetString(element, "url") ?? string.Empty;
        var className = GetString(element, "_class") ?? string.Empty;

        var hasChildrenProperty = element.TryGetProperty("jobs", out var children) && children.ValueKind == JsonValueKind.Array;
        var looksLikeContainer = ContainerClassMarkers.Any(m => className.Contains(m, StringComparison.OrdinalIgnoreCase));

        var node = new JobNode
        {
            Name = name,
            FullName = fullName,
            Path = ToRelativePath(url, fullName),
            Color = GetString(element, "color"),
            IsFolder = hasChildrenProperty || looksLikeContainer,
            Buildable = element.TryGetProperty("buildable", out var b) && b.ValueKind == JsonValueKind.True,
        };

        if (hasChildrenProperty)
            foreach (var child in children.EnumerateArray())
                node.Children.Add(ReadJobNode(child));

        return node;
    }

    /// <summary>
    /// Turns the absolute URL Jenkins reports into a path relative to <see cref="BaseUri"/>.
    /// We deliberately re-anchor on the configured URL: a Jenkins whose "Jenkins URL" setting is
    /// wrong would otherwise hand us unreachable hostnames.
    /// </summary>
    private string ToRelativePath(string url, string fullName)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath;
            var basePath = BaseUri.AbsolutePath;

            if (path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                path = path[basePath.Length..];

            path = path.TrimStart('/');
            if (path.Length > 0)
                return path.EndsWith('/') ? path : path + "/";
        }

        // Fallback: rebuild from the full name (segments are already URL-encoded by Jenkins).
        var segments = fullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? string.Empty : string.Concat(segments.Select(s => $"job/{s}/"));
    }

    // ---------------------------------------------------------------- polling

    private const string SnapshotTree =
        "name,fullName,displayName,color," +
        "lastCompletedBuild[number,displayName,url,result,timestamp,duration," +
        "actions[failCount,skipCount,totalCount]]";

    public async Task<JobSnapshot> GetJobSnapshotAsync(MonitoredJob job, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"{job.Path}api/json?tree={SnapshotTree}", ct).ConfigureAwait(false);
        EnsureSuccess(response);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;

        var (status, building) = StatusMap.FromColor(GetString(root, "color"));
        var displayName = GetString(root, "displayName") ?? GetString(root, "name") ?? job.DisplayName;

        BuildInfo? lastCompleted = null;
        if (root.TryGetProperty("lastCompletedBuild", out var build) && build.ValueKind == JsonValueKind.Object)
        {
            var number = GetInt(build, "number") ?? 0;
            var result = StatusMap.FromResult(GetString(build, "result"));
            var timestampMs = GetLong(build, "timestamp") ?? 0;
            var durationMs = GetLong(build, "duration") ?? 0;

            lastCompleted = new BuildInfo(
                Number: number,
                DisplayName: GetString(build, "displayName") ?? $"#{number}",
                Path: ToRelativePath(GetString(build, "url") ?? string.Empty, $"{job.FullName}/{number}"),
                Result: result,
                Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(timestampMs),
                Duration: TimeSpan.FromMilliseconds(durationMs),
                Tests: ReadTestSummary(build));

            // A job whose colour we could not read still has a usable last result.
            if (status == BuildStatus.Unknown)
                status = result;
        }

        return new JobSnapshot(job.FullName, displayName, job.Path, status, building, lastCompleted);
    }

    /// <summary>Pulls JUnit counters straight out of the build's action list — no extra round-trip.</summary>
    private static TestSummary? ReadTestSummary(JsonElement build)
    {
        if (!build.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object)
                continue;

            var total = GetInt(action, "totalCount");
            if (total is null)
                continue;

            return new TestSummary(total.Value, GetInt(action, "failCount") ?? 0, GetInt(action, "skipCount") ?? 0);
        }

        return null;
    }

    /// <summary>Fallback when the build action carried no counters (some aggregated reports).</summary>
    public async Task<TestSummary?> GetTestReportAsync(string buildPath, CancellationToken ct)
    {
        try
        {
            using var response = await _http
                .GetAsync($"{buildPath}testReport/api/json?tree=totalCount,failCount,skipCount,passCount", ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;

            var failed = GetInt(root, "failCount") ?? 0;
            var skipped = GetInt(root, "skipCount") ?? 0;
            var total = GetInt(root, "totalCount") ?? ((GetInt(root, "passCount") ?? 0) + failed + skipped);

            return total == 0 ? null : new TestSummary(total, failed, skipped);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- triggering

    /// <summary>
    /// Asks Jenkins to queue a build of the job. Returns as soon as the request is accepted: the
    /// build itself only shows up on a later poll, once its quiet period has elapsed.
    /// </summary>
    public async Task TriggerBuildAsync(MonitoredJob job, CancellationToken ct)
    {
        var response = await PostAsync($"{job.Path}build", ct).ConfigureAwait(false);

        // A parameterised job turns /build down; the same request on buildWithParameters starts it
        // with the default value of every parameter.
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.MethodNotAllowed)
        {
            response.Dispose();
            response = await PostAsync($"{job.Path}buildWithParameters", ct).ConfigureAwait(false);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return;

            throw new JenkinsException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => Loc.T("Jenkins_Unauthorized"),
                HttpStatusCode.Forbidden => Loc.T("Jenkins_ForbiddenBuild"),
                HttpStatusCode.NotFound => Loc.T("Jenkins_JobNotFound"),
                HttpStatusCode.Conflict => Loc.T("Jenkins_BuildRefused"),
                _ => $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
            });
        }
    }

    /// <summary>
    /// A POST with the crumb we hold, if any. A 403 on a write is far more often a missing or stale
    /// CSRF crumb than a permission problem, so one fresh crumb buys the request a second chance.
    /// </summary>
    private async Task<HttpResponseMessage> PostAsync(string path, CancellationToken ct)
    {
        var response = await SendPostAsync(path, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Forbidden)
            return response;

        if (!await RefreshCrumbAsync(ct).ConfigureAwait(false))
            return response;

        response.Dispose();
        return await SendPostAsync(path, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendPostAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (_crumb is { } crumb)
            request.Headers.TryAddWithoutValidation(crumb.Field, crumb.Value);

        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches a crumb. False when the instance has no crumb issuer — CSRF protection is off, and
    /// retrying the request would change nothing.
    /// </summary>
    private async Task<bool> RefreshCrumbAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http
                .GetAsync("crumbIssuer/api/json?tree=crumb,crumbRequestField", ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return false;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var field = GetString(doc.RootElement, "crumbRequestField");
            var value = GetString(doc.RootElement, "crumb");

            if (string.IsNullOrEmpty(field) || value is null)
                return false;

            _crumb = new Crumb(field, value);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return false;
        }
    }

    private sealed record Crumb(string Field, string Value);

    // ---------------------------------------------------------------- coverage

    /// <summary>
    /// Coverage has no single standard on Jenkins, so we probe the endpoints of the common plugins
    /// in order of modernity and keep the first one that yields numbers.
    /// </summary>
    private static readonly (string Endpoint, string Source)[] CoverageProbes =
    [
        ("coverage/api/json?tree=projectStatistics[*]", "Coverage"),   // Coverage plugin (io.jenkins.plugins.coverage.metrics)
        ("coverage/result/api/json?depth=2", "Code Coverage API"),      // Code Coverage API plugin v1
        ("jacoco/api/json?depth=1", "JaCoCo"),
        ("cobertura/api/json?depth=2", "Cobertura"),
        ("coverage/api/json?depth=1", "Coverage"),                      // last resort, heavier payload
    ];

    public async Task<CoverageSummary?> GetCoverageAsync(string buildPath, CancellationToken ct)
    {
        foreach (var (endpoint, source) in CoverageProbes)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var response = await _http.GetAsync(buildPath + endpoint, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(body))
                    continue;

                using var doc = JsonDocument.Parse(body);
                var coverage = ParseCoverage(doc.RootElement, source);
                if (coverage is not null)
                    return coverage;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                // Plugin absent or endpoint shaped differently — try the next one.
            }
        }

        return null;
    }

    internal static CoverageSummary? ParseCoverage(JsonElement root, string source)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        // Coverage plugin: projectStatistics = { "line": "87.99%", "branch": "81.35%", ... }
        if (root.TryGetProperty("projectStatistics", out var stats) && stats.ValueKind == JsonValueKind.Object)
        {
            var line = ReadPercentValue(stats, "line");
            var branch = ReadPercentValue(stats, "branch");
            if (line.HasValue || branch.HasValue)
                return new CoverageSummary(line, branch, source);
        }

        // JaCoCo plugin: lineCoverage = { percentageFloat: 87.99, covered: .., missed: .. }
        var jacocoLine = ReadRatioObject(root, "lineCoverage");
        var jacocoBranch = ReadRatioObject(root, "branchCoverage");
        if (jacocoLine.HasValue || jacocoBranch.HasValue)
            return new CoverageSummary(jacocoLine, jacocoBranch, source);

        // Cobertura / Code Coverage API: results.elements = [ { name: "Lines", ratio: 87.9 }, ... ]
        var elements = FindElementsArray(root);
        if (elements.HasValue)
        {
            double? line = null, branch = null;

            foreach (var element in elements.Value.EnumerateArray())
            {
                var name = GetString(element, "name")?.ToLowerInvariant();
                if (name is null)
                    continue;

                var ratio = GetDouble(element, "ratio") ?? GetDouble(element, "percentageFloat") ?? GetDouble(element, "percentage");
                if (ratio is null)
                    continue;

                if (name.Contains("line"))
                    line ??= ratio;
                else if (name.Contains("branch") || name.Contains("conditional"))
                    branch ??= ratio;
            }

            if (line.HasValue || branch.HasValue)
                return new CoverageSummary(line, branch, source);
        }

        return null;
    }

    private static JsonElement? FindElementsArray(JsonElement root)
    {
        if (root.TryGetProperty("elements", out var direct) && direct.ValueKind == JsonValueKind.Array)
            return direct;

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Object
            && results.TryGetProperty("elements", out var nested) && nested.ValueKind == JsonValueKind.Array)
            return nested;

        return null;
    }

    /// <summary>Reads a percentage that may come as a number (87.99) or a string ("87,99%").</summary>
    private static double? ReadPercentValue(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
            return null;

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.GetDouble();

            case JsonValueKind.String:
                var text = value.GetString()?.Replace("%", string.Empty).Replace(',', '.').Trim();
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

            default:
                return null;
        }
    }

    private static double? ReadRatioObject(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
            return null;

        var percentage = GetDouble(value, "percentageFloat") ?? GetDouble(value, "percentage");
        if (percentage.HasValue)
            return percentage;

        var covered = GetDouble(value, "covered");
        var missed = GetDouble(value, "missed");
        if (covered.HasValue && missed.HasValue && covered + missed > 0)
            return covered.Value * 100d / (covered.Value + missed.Value);

        return null;
    }

    // ---------------------------------------------------------------- helpers

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        throw new JenkinsException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => Loc.T("Jenkins_Unauthorized"),
            HttpStatusCode.Forbidden => Loc.T("Jenkins_ForbiddenRead"),
            HttpStatusCode.NotFound => Loc.T("Jenkins_ResourceNotFound"),
            HttpStatusCode.ProxyAuthenticationRequired => Loc.T("Jenkins_ProxyAuthRequired"),
            _ => $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
        });
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var result)
            ? result
            : null;

    private static long? GetLong(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var result)
            ? result
            : null;

    private static double? GetDouble(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var result)
            ? result
            : null;

    public void Dispose() => _http.Dispose();
}

public sealed class JenkinsException(string message) : Exception(message);
