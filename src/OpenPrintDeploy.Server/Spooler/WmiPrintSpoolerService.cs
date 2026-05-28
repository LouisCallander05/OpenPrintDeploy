using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;

namespace OpenPrintDeploy.Server.Spooler;

/// <summary>
/// Reads shared printers from the local Windows print spooler via WMI
/// (<c>Win32_Printer</c>). The <c>Local = TRUE</c> filter excludes network
/// printer connections this server happens to have, so we list only the
/// queues the print server itself publishes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiPrintSpoolerService : IPrintSpoolerService
{
    private const string Query =
        "SELECT Name, ShareName, DriverName, Comment FROM Win32_Printer WHERE Shared = TRUE AND Local = TRUE";

    private readonly SpoolerOptions _options;
    private readonly ILogger<WmiPrintSpoolerService> _logger;

    public WmiPrintSpoolerService(IOptions<SpoolerOptions> options, ILogger<WmiPrintSpoolerService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DiscoveredPrinter>> GetSharedPrintersAsync(CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(EnumerateShared, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spooler enumeration failed; the admin import panel will be empty.");
            return [];
        }
    }

    private List<DiscoveredPrinter> EnumerateShared()
    {
        using var searcher = new ManagementObjectSearcher(Query);
        using var collection = searcher.Get();

        var printers = new List<DiscoveredPrinter>(collection.Count);
        foreach (ManagementObject mo in collection)
        {
            using (mo)
            {
                var share = StringProp(mo, "ShareName");
                if (string.IsNullOrWhiteSpace(share))
                {
                    // A Shared=TRUE row with no ShareName is unusable for a UNC.
                    continue;
                }

                var name = StringProp(mo, "Name") ?? share;
                printers.Add(new DiscoveredPrinter(
                    ShareName: share.Trim(),
                    DisplayName: name.Trim(),
                    Driver: NullIfBlank(StringProp(mo, "DriverName")),
                    Comment: NullIfBlank(StringProp(mo, "Comment")),
                    UncPath: SpoolerUnc.Build(_options.ServerName, share)));
            }
        }

        return printers
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? StringProp(ManagementBaseObject mo, string name)
        => mo[name] as string;

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
