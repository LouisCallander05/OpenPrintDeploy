namespace OpenPrintDeploy.Shared.Sync;

/// <summary>
/// The client's sync request. For v0 the identifying facts come in the body;
/// production deployments will pull <see cref="Username"/> from the Negotiate
/// auth context and resolve <see cref="GroupSids"/> server-side via LDAP
/// using a service account. The client should never be trusted to assert its
/// own group membership.
/// </summary>
public sealed record SyncRequestDto(
    string Username,
    IReadOnlyList<string> GroupSids,
    string? MachineOuDn,
    string? ClientIp);
