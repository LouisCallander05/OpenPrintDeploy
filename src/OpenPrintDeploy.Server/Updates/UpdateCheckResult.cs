namespace OpenPrintDeploy.Server.Updates;

/// <summary>
/// Outcome of an update check, for the admin UI to render. Exactly one of
/// (<see cref="Error"/>) or the version fields is meaningful.
/// </summary>
public sealed record UpdateCheckResult
{
    /// <summary>The version this server is running (e.g. "0.5.2").</summary>
    public required string CurrentVersion { get; init; }

    /// <summary>The latest published release version, with no leading "v".</summary>
    public string? LatestVersion { get; init; }

    /// <summary>True when the latest release is newer than what's running.</summary>
    public bool UpdateAvailable { get; init; }

    /// <summary>The GitHub release page, for the "release notes" link.</summary>
    public string? ReleaseUrl { get; init; }

    /// <summary>When the latest release was published.</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>A user-facing reason the check couldn't complete, if any.</summary>
    public string? Error { get; init; }

    public static UpdateCheckResult Failed(string current, string error) =>
        new() { CurrentVersion = current, Error = error };
}
