namespace OpenPrintDeploy.Server.Data.Entities;

/// <summary>A printer connection every syncing client must remove.</summary>
public sealed class RemovedPrinterEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>UNC path, e.g. <c>\\printsrv01\Retired-Queue</c>.</summary>
    public required string UncPath { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
