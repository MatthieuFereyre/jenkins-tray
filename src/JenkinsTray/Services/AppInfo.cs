using System.Reflection;

namespace JenkinsTray.Services;

/// <summary>Identity of the running build, as the executable itself declares it.</summary>
public static class AppInfo
{
    /// <summary>Version of the running build: "1.1.0".</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>The same version, as the interface shows it: "v1.1.0".</summary>
    public static string DisplayVersion { get; } = "v" + Version;

    /// <summary>
    /// Read from the assembly rather than from the file on disk: it carries the same
    /// <c>&lt;Version&gt;</c>, and stays right when the app runs behind a host executable.
    /// </summary>
    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;

        var raw = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();

        if (string.IsNullOrWhiteSpace(raw))
            return "?";

        // The SDK appends the source revision — "1.1.0+9a1c3f" — which says nothing to a reader.
        var plus = raw.IndexOf('+');
        return plus < 0 ? raw : raw[..plus];
    }
}
