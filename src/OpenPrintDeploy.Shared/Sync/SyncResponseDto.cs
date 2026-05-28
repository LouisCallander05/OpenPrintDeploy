namespace OpenPrintDeploy.Shared.Sync;

public sealed record SyncResponseDto(IReadOnlyList<PrinterDto> Printers);
