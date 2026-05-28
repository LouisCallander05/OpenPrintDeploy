namespace OpenPrintDeploy.Server.Spooler;

/// <summary>
/// Fallback spooler used on non-Windows hosts (developer Linux/macOS boxes,
/// CI). Reports no printers so the admin UI's import panel cleanly shows
/// "nothing to import" instead of crashing on a WMI call that only works on
/// Windows. Production always runs <see cref="WmiPrintSpoolerService"/>.
/// </summary>
public sealed class StubPrintSpoolerService : IPrintSpoolerService
{
    public Task<IReadOnlyList<DiscoveredPrinter>> GetSharedPrintersAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DiscoveredPrinter>>([]);
}
