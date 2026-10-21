namespace OpenPrintDeploy.Shared.Sync;

public sealed record PrinterDto(string DisplayName, string UncPath)
{
    // Short-lived server-issued capability. Never a path or an installation command.
    public string? DriverTicket { get; init; }
}
