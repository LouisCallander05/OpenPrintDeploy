namespace OpenPrintDeploy.Server.Directory;

/// <summary>
/// Resolves identity facts the server must not trust the client to assert:
/// the user's transitive group SIDs, and the group catalog used to pick rule
/// targets in the admin UI. The LDAP implementation talks to on-prem AD; a
/// stub implementation backs local development where no directory is reachable.
/// </summary>
public interface IDirectoryService
{
    /// <summary>
    /// The transitive set of group SIDs for <paramref name="username"/>
    /// (a bare sAMAccountName), together with whether the directory was actually
    /// reachable. A reachable directory that simply has no groups for the user
    /// returns <see cref="GroupResolution.Available"/> = true with an empty set;
    /// a directory that could not be consulted at all (DC outage, bind failure)
    /// returns <see cref="GroupResolution.Unavailable"/>. Callers that must fail
    /// closed (admin authorization) can ignore the flag and read
    /// <see cref="GroupResolution.Sids"/>; the sync path uses the flag so a
    /// directory outage never tears down a user's printers.
    /// </summary>
    Task<GroupResolution> GetGroupSidsAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// Validates a domain username + password by attempting an LDAP bind as that
    /// user — used by the admin UI's Basic authentication. Returns false on bad
    /// credentials or any directory error (fail closed). The default returns
    /// false; real providers override it.
    /// </summary>
    Task<bool> ValidateCredentialsAsync(string username, string password, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>
    /// Resolves a group NAME to its SID, searching the whole forest (so an admin
    /// group in another domain resolves). Returns null if no such group is found
    /// or the directory can't be reached. The default returns null; real
    /// providers override it.
    /// </summary>
    Task<string?> ResolveGroupSidByNameAsync(string name, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// Groups whose name matches <paramref name="query"/> (a case-insensitive
    /// substring; empty matches the first <paramref name="limit"/> groups),
    /// for the admin zone-rule picker. Returns an empty list if the directory
    /// can't be reached — the caller falls back to raw-SID entry.
    /// </summary>
    Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(
        string query, int limit, CancellationToken ct = default);

    /// <summary>
    /// The friendly name of the group with the given <paramref name="sid"/>, or
    /// null if it can't be resolved (unknown SID or directory unreachable). Used
    /// only to label existing rules; matching always keys off the stored SID.
    /// </summary>
    Task<string?> ResolveGroupNameAsync(string sid, CancellationToken ct = default);

    /// <summary>
    /// A one-shot health check the admin UI calls to confirm directory config
    /// works on the joined server: bind, report the effective endpoint, do a
    /// trivial sample lookup. Errors are reported in the result, not thrown.
    /// </summary>
    Task<DirectoryDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default);
}

/// <summary>A directory group as the admin UI needs it: its stable SID (what a
/// zone rule stores and matches on) plus a human-readable name for display.</summary>
public sealed record DirectoryGroup(string Sid, string Name);

/// <summary>
/// The outcome of resolving a user's group SIDs: the transitive SID set plus
/// whether the directory could actually be consulted. The distinction matters
/// only for the sync path — an empty set with <see cref="Available"/> = false
/// means "directory unavailable, change nothing", whereas an empty set with
/// <see cref="Available"/> = true means "the user genuinely has no groups".
/// </summary>
public sealed record GroupResolution(IReadOnlySet<string> Sids, bool Available)
{
    /// <summary>The directory was reachable; <paramref name="sids"/> is authoritative (may be empty).</summary>
    public static GroupResolution Resolved(IReadOnlySet<string> sids) => new(sids, Available: true);

    /// <summary>The directory could not be consulted — callers should make no destructive changes.</summary>
    public static GroupResolution Unavailable { get; } =
        new(new HashSet<string>(StringComparer.Ordinal), Available: false);
}

/// <summary>
/// What the admin UI shows on "Test directory connection": which provider, the
/// effective endpoint (after auto-discovery), whether the bind succeeded, a
/// sample lookup count, and an error message when it didn't.
/// </summary>
public sealed record DirectoryDiagnostics(
    string Provider,
    string AuthMode,
    string? Server,
    string? SearchBase,
    bool Connected,
    int? SampleGroupCount,
    string? Error);
