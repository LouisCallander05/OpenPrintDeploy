namespace OpenPrintDeploy.Server.Directory;

/// <summary>
/// Resolves identity facts the server must not trust the client to assert:
/// the user's transitive group SIDs and (best-effort) the machine's OU. The
/// LDAP implementation talks to on-prem AD; a stub implementation backs local
/// development where no directory is reachable.
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
    /// The distinguished name of the OU containing <paramref name="machineName"/>,
    /// or null if the machine has no on-prem computer object (the normal case
    /// for Entra-joined endpoints) or can't be resolved.
    /// </summary>
    Task<string?> GetMachineOuDnAsync(string machineName, CancellationToken ct = default);
}
