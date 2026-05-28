namespace OpenPrintDeploy.Server.Data.Entities;

public sealed class ZoneEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    /// <summary>Display sort order in the admin UI — higher first. Has no effect on evaluation.</summary>
    public int Priority { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ZoneRuleEntity> Rules { get; set; } = new List<ZoneRuleEntity>();
    public ICollection<PrinterEntity> Printers { get; set; } = new List<PrinterEntity>();
}
