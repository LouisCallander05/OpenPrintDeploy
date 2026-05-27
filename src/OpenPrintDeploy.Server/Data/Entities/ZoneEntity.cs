namespace OpenPrintDeploy.Server.Data.Entities;

public sealed class ZoneEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    /// <summary>Higher priority wins when resolving the default printer.</summary>
    public int Priority { get; set; }

    /// <summary>
    /// Nullable FK to one of the printers assigned to this zone. Not enforced
    /// at the DB layer that the default is in <see cref="Printers"/>; the
    /// evaluator guards against drift at runtime.
    /// </summary>
    public Guid? DefaultPrinterId { get; set; }
    public PrinterEntity? DefaultPrinter { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ZoneRuleEntity> Rules { get; set; } = new List<ZoneRuleEntity>();
    public ICollection<PrinterEntity> Printers { get; set; } = new List<PrinterEntity>();
}
