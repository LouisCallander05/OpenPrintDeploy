namespace OpenPrintDeploy.Shared.Sync;

/// <summary>The outcome of a sync after the tray has applied it locally.</summary>
public sealed record SyncReportDto(
    Guid SyncId,
    string? MachineName,
    string? ClientVersion,
    SyncReportStatus Status,
    IReadOnlyList<PrinterSyncResultDto> Printers,
    string? Error = null);

public enum SyncReportStatus
{
    Synced,
    Partial,
    Deferred,
    Failed,
}

/// <summary>A single printer operation performed during a sync.</summary>
public sealed record PrinterSyncResultDto(
    string? DisplayName,
    string UncPath,
    PrinterSyncOperation Operation,
    bool Succeeded,
    string? Error = null,
    PrinterRemovalReason RemovalReason = PrinterRemovalReason.None,
    bool AlreadyAbsent = false);

public enum PrinterSyncOperation
{
    Present,
    Installed,
    Adopted,
    Removed,
}

public enum PrinterRemovalReason
{
    None,
    NoLongerAssigned,
    GlobalRemoval,
}
