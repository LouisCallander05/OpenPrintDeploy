namespace OpenPrintDeploy.Shared.Sync;

/// <summary>
/// The client's sync request. Identity is established server-side from the
/// authenticated connection (Negotiate/Kerberos), and group membership is
/// resolved server-side via LDAP — the client never asserts who it is or what
/// groups it belongs to. <see cref="MachineName"/>, <see cref="DeviceId"/>, and
/// <see cref="ClientVersion"/> are diagnostic metadata. <see cref="DeviceId"/>
/// is a one-way identifier derived from the Windows installation and prevents
/// equal or truncated hostnames from merging dashboard records. <see cref="SyncId"/>
/// correlates the later result report; none of these fields are used for
/// authorization or assignment decisions.
/// </summary>
public sealed record SyncRequestDto(
    string? MachineName,
    Guid? SyncId = null,
    string? ClientVersion = null,
    string? DeviceId = null);
