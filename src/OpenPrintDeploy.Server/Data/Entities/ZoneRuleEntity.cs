namespace OpenPrintDeploy.Server.Data.Entities;

public sealed class ZoneRuleEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ZoneId { get; set; }
    public ZoneEntity? Zone { get; set; }

    public string? GroupSid { get; set; }
    public string? SubnetCidr { get; set; }
    public string? OuDn { get; set; }
}
