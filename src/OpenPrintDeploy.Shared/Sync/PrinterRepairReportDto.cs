namespace OpenPrintDeploy.Shared.Sync;

public enum PrinterRepairStatus { Pending, Running, Completed, Failed }

public sealed record PrinterRepairReportDto(
    Guid RequestId, string DeviceId, PrinterRepairStatus Status, string? Error = null);
