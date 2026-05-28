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
    /// (a bare sAMAccountName). Returns an empty set if the user can't be
    /// resolved — callers treat that as "no matching zones", never an error.
    /// </summary>
    Task<IReadOnlySet<string>> GetGroupSidsAsync(string username, CancellationToken ct = default);

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
