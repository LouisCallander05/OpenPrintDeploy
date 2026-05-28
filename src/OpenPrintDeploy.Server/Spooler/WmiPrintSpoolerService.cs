using System.Management;
using System.Net.NetworkInformation;
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
    private readonly Lazy<string> _effectiveServerName;

    public WmiPrintSpoolerService(IOptions<SpoolerOptions> options, ILogger<WmiPrintSpoolerService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _effectiveServerName = new Lazy<string>(ResolveServerName, LazyThreadSafetyMode.ExecutionAndPublication);
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

        var server = _effectiveServerName.Value;

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
                    UncPath: SpoolerUnc.Build(server, share)));
            }
        }

        return printers
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Server name baked into the imported UNCs. Explicit config wins; on a
    /// domain-joined host we prefer the FQDN over the bare NetBIOS name so the
    /// UNC resolves cleanly from clients regardless of their DNS suffix search
    /// list. A workgroup machine (no <c>DomainName</c>) falls back to the
    /// hostname.
    /// </summary>
    private string ResolveServerName()
    {
        if (!string.IsNullOrWhiteSpace(_options.ServerName))
        {
            return _options.ServerName.Trim();
        }

        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            var host = string.IsNullOrWhiteSpace(props.HostName) ? Environment.MachineName : props.HostName;
            var domain = props.DomainName?.Trim();
            var server = string.IsNullOrWhiteSpace(domain) ? host : $"{host}.{domain}";
            _logger.LogInformation("Spooler UNCs will use server '{Server}' (FQDN resolved automatically).", server);
            return server;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FQDN lookup failed; falling back to NetBIOS hostname for spooler UNCs.");
            return Environment.MachineName;
        }
    }

    private static string? StringProp(ManagementBaseObject mo, string name)
        => mo[name] as string;

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
