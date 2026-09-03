using System.IO;

namespace JenkinsTray.Services;

/// <summary>Best-effort diagnostics written next to the settings file.</summary>
public static class AppLog
{
    private static readonly Lock Gate = new();

    public static string FilePath => Path.Combine(SettingsStore.DataDirectory, "jenkins-tray.log");

    public static void Warn(string context, Exception exception) => Write($"{context}: {exception}");

    public static void Info(string message) => Write(message);

    private static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.DataDirectory);
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:u}  {line}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never be the thing that breaks the app.
        }
    }
}
