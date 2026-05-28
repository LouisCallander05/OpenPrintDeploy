namespace OpenPrintDeploy.Shared.Sync;

/// <summary>
/// The client's sync request. Identity is established server-side from the
/// authenticated connection (Negotiate/Kerberos), and group membership is
/// resolved server-side via LDAP — the client never asserts who it is or what
/// groups it belongs to. <see cref="MachineName"/> is included only for the
/// server's diagnostic logging.
/// </summary>
public sealed record SyncRequestDto(string? MachineName);
