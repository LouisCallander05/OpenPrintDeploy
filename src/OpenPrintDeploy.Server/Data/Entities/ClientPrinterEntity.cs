namespace OpenPrintDeploy.Server.Data.Entities;

public sealed class ClientPrinterEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientUserId { get; set; }
    public string? DisplayName { get; set; }
    public required string UncPath { get; set; }
    public required string NormalizedUncPath { get; set; }
    public string Status { get; set; } = ClientPrinterStatuses.Pending;
    public string? LastOperation { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public required ClientUserEntity ClientUser { get; set; }
}

public static class ClientPrinterStatuses
{
    public const string Pending = "Pending";
    public const string Present = "Synced";
    public const string Failed = "Failed";
    public const string RemovalPending = "Removal pending";
    public const string RemovalFailed = "Removal failed";
}
