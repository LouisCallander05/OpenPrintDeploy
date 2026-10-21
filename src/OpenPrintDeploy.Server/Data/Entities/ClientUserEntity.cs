namespace OpenPrintDeploy.Server.Data.Entities;

public sealed class ClientUserEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public required string Username { get; set; }
    public required string NormalizedUsername { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? LastSyncId { get; set; }
    public DateTimeOffset? LastSyncStartedAt { get; set; }
    public DateTimeOffset? LastSyncCompletedAt { get; set; }
    public string LastSyncStatus { get; set; } = ClientSyncStatuses.Unreported;
    public int AssignedPrinterCount { get; set; }
    public int SyncedPrinterCount { get; set; }
    public int FailedPrinterCount { get; set; }
    public string? LastError { get; set; }

    public required ClientDeviceEntity Device { get; set; }
    public ICollection<ClientPrinterEntity> Printers { get; set; } = new List<ClientPrinterEntity>();
    public ICollection<ClientActivityEntity> Activities { get; set; } = new List<ClientActivityEntity>();
}

public static class ClientSyncStatuses
{
    public const string Syncing = "Syncing";
    public const string Synced = "Synced";
    public const string Partial = "Partial";
    public const string Deferred = "Deferred";
    public const string Failed = "Failed";
    public const string Unreported = "Unreported";
}
