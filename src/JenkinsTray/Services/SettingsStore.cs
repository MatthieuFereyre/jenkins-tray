using System.IO;
using System.Text.Json;
using JenkinsTray.Models;

namespace JenkinsTray.Services;

public sealed class SettingsStore
{
    /// <summary>How many previous versions of settings.json are kept alongside it.</summary>
    private const int BackupGenerations = 3;

    private static string? _dataDirectoryOverride;

    /// <summary>
    /// Where settings, logs and generated icons live. Defaults to <c>%APPDATA%\JenkinsTray</c>.
    /// </summary>
    public static string DataDirectory => _dataDirectoryOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JenkinsTray");

    /// <summary>
    /// Redirects every file the app writes to another folder. Meant for running a throwaway instance
    /// (tests, demos, a second profile) without ever touching the real configuration.
    /// Must be called before anything reads <see cref="DataDirectory"/>.
    /// </summary>
    public static void UseDataDirectory(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            _dataDirectoryOverride = Path.GetFullPath(path);
    }

    public string FilePath { get; } = Path.Combine(DataDirectory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, SettingsJson.Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt file must not stop the app from starting: keep a copy and fall back to defaults.
            TryBackupCorruptFile();
            return new AppSettings();
        }
    }

    /// <summary>
    /// <paramref name="rotateBackups"/> is false for a write that only carries a display preference:
    /// a handful of them would otherwise push every recoverable version of the servers and the
    /// monitored jobs out of the backup generations.
    /// </summary>
    public void Save(AppSettings settings, bool rotateBackups = true)
    {
        Directory.CreateDirectory(DataDirectory);
        var json = JsonSerializer.Serialize(settings, SettingsJson.Options);

        // A no-op save would otherwise burn a backup generation for nothing.
        if (File.Exists(FilePath) && ReadOrNull(FilePath) == json)
            return;

        if (rotateBackups)
            RotateBackups();

        // Write to a temporary file first so an interrupted save cannot truncate the real one.
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, overwrite: true);
    }

    public string BackupPath(int generation) => Path.Combine(DataDirectory, $"settings.backup{generation}.json");

    /// <summary>
    /// Keeps the last few versions of the file, so an unwanted overwrite stays recoverable instead
    /// of being final.
    /// </summary>
    private void RotateBackups()
    {
        if (!File.Exists(FilePath))
            return;

        try
        {
            for (var generation = BackupGenerations - 1; generation >= 1; generation--)
            {
                var older = BackupPath(generation);
                if (File.Exists(older))
                    File.Move(older, BackupPath(generation + 1), overwrite: true);
            }

            File.Copy(FilePath, BackupPath(1), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Rotation des sauvegardes de la configuration", ex);
        }
    }

    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Move(FilePath, FilePath + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
            // Nothing sensible to do here.
        }
    }
}
