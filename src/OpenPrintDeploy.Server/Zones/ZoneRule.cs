namespace OpenPrintDeploy.Server.Zones;

/// <summary>
/// A single matching condition on a zone: the user must be a member of the
/// group with the given SID for the rule to match. A rule with no GroupSid
/// matches nothing — that shape is treated as misconfiguration, not a wildcard.
/// </summary>
public sealed record ZoneRule(string? GroupSid);
