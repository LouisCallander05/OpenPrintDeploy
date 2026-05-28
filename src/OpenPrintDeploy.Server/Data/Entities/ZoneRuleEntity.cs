namespace OpenPrintDeploy.Server.Data.Entities;

public sealed class ZoneRuleEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ZoneId { get; set; }
    public ZoneEntity? Zone { get; set; }

    /// <summary>
    /// The group SID that gates this rule. A rule with no group set matches
    /// nothing — the admin form rejects that shape so it never reaches storage.
    /// </summary>
    public string? GroupSid { get; set; }
}
