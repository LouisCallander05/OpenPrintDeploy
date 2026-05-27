namespace OpenPrintDeploy.Server.Data.Entities;

public sealed class PrinterEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>UNC path, e.g. <c>\\printsrv01\HR-MFP-01</c>. Unique.</summary>
    public required string UncPath { get; set; }

    /// <summary>Human-readable name shown in Devices and Printers.</summary>
    public required string DisplayName { get; set; }

    /// <summary>Optional location string (room, building) shown in Windows.</summary>
    public string? Location { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ZoneEntity> Zones { get; set; } = new List<ZoneEntity>();
}
