namespace OpenPrintDeploy.Server.Data.Entities;

public sealed class ClientActivityEntity
{
    public long Id { get; set; }
    public Guid ClientUserId { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public required string Type { get; set; }
    public required string Summary { get; set; }
    public Guid? SyncId { get; set; }
    public string? PrinterDisplayName { get; set; }
    public string? PrinterUncPath { get; set; }
    public string? Error { get; set; }

    public required ClientUserEntity ClientUser { get; set; }
}
