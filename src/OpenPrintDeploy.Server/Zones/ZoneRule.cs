namespace OpenPrintDeploy.Server.Zones;

/// <summary>
/// A single matching condition on a zone. Within a rule, every non-null
/// criterion must match the evaluation context; null criteria are wildcards.
/// A rule with no criteria at all matches nothing — that shape is treated as
/// misconfiguration rather than a global wildcard.
/// </summary>
public sealed record ZoneRule(
    string? GroupSid,
    string? SubnetCidr,
    string? OuDn);
