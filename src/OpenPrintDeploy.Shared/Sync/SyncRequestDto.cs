namespace OpenPrintDeploy.Shared.Sync;

/// <summary>
/// The client's sync request. Identity is established server-side from the
/// authenticated connection (Negotiate/Kerberos), and group membership is
/// resolved server-side via LDAP — the client never asserts who it is or what
/// groups it belongs to. The only fact the client contributes is its own
/// machine name, used for a best-effort OU lookup. That value is client-asserted
/// (hence spoofable), so OU rules are a coarse grouping, not a security boundary.
/// </summary>
public sealed record SyncRequestDto(string? MachineName);
