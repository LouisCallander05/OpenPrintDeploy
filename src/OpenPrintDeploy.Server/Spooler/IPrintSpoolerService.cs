namespace OpenPrintDeploy.Server.Spooler;

/// <summary>
/// Discovers the shared printers the local Windows print spooler is publishing,
/// for the admin "Import from spooler" workflow. Discovery is read-only and
/// never throws to the UI: a non-Windows host, an unreachable spooler, or a WMI
/// failure all surface as an empty list (logged), so the admin can fall back to
/// manual entry.
/// </summary>
public interface IPrintSpoolerService
{
    Task<IReadOnlyList<DiscoveredPrinter>> GetSharedPrintersAsync(CancellationToken ct = default);
}

/// <summary>One shared printer as the spooler reports it, ready for import.</summary>
/// <param name="ShareName">The share name (the right-hand side of the UNC).</param>
/// <param name="DisplayName">The queue's user-visible name (Win32_Printer.Name).</param>
/// <param name="Driver">The driver name, for the admin to sanity-check.</param>
/// <param name="Comment">The spooler "Comment" field, often a description.</param>
/// <param name="UncPath">Pre-built <c>\\server\share</c>, using the configured server name.</param>
public sealed record DiscoveredPrinter(
    string ShareName,
    string DisplayName,
    string? Driver,
    string? Comment,
    string UncPath);
