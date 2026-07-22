namespace OpenPrintDeploy.Shared.Sync;

/// <summary>
/// The client's sync request. Identity is established server-side from the
/// authenticated connection (Negotiate/Kerberos), and group membership is
/// resolved server-side via LDAP — the client never asserts who it is or what
/// groups it belongs to. <see cref="MachineName"/> and <see cref="ClientVersion"/>
/// are diagnostic metadata. <see cref="SyncId"/> correlates the later result
/// report and is not used for authorization or assignment decisions.
/// </summary>
public sealed record SyncRequestDto(
    string? MachineName,
    Guid? SyncId = null,
    string? ClientVersion = null);
