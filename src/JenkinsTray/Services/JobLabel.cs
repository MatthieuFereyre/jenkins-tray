namespace JenkinsTray.Services;

/// <summary>
/// Splits a Jenkins <c>fullName</c> — <c>my-team/my-app/master</c> — into
/// the pieces the UI names a job by. A multibranch branch is named after its branch alone, so
/// "master" on its own identifies nothing: everywhere a job is announced we pair the leaf with the
/// project holding it.
/// </summary>
public static class JobLabel
{
    /// <summary>Separator between two levels of the hierarchy.</summary>
    public const string Chevron = " › ";

    /// <summary>Jenkins URL-encodes slashes inside a job name, so segments never split a branch name.</summary>
    private static string[] Split(string? fullName) =>
        (fullName ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Name of the job itself, without any of its folders.</summary>
    public static string Leaf(string? fullName)
    {
        var segments = Split(fullName);
        return segments.Length == 0 ? string.Empty : segments[^1];
    }

    /// <summary>Folder or multibranch project directly holding the job. Empty at the top level.</summary>
    public static string Project(string? fullName)
    {
        var segments = Split(fullName);
        return segments.Length < 2 ? string.Empty : segments[^2];
    }

    /// <summary>Folders above the project, already spaced for display. Empty when there are none.</summary>
    public static string Ancestors(string? fullName)
    {
        var segments = Split(fullName);
        return segments.Length < 3 ? string.Empty : string.Join(Chevron, segments[..^2]);
    }

    /// <summary>
    /// The job named the way it must be read: <c>project › leaf</c>, or just the leaf for a job that
    /// sits at the root. <paramref name="displayName"/> — what Jenkins reports for the job — wins over
    /// the last segment when it is set.
    /// </summary>
    public static string Qualified(string? fullName, string? displayName = null)
    {
        var leaf = string.IsNullOrWhiteSpace(displayName) ? Leaf(fullName) : displayName!;
        var project = Project(fullName);
        return project.Length == 0 ? leaf : project + Chevron + leaf;
    }
}
