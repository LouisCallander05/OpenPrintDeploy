using System.IO;
using System.Text.Json;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>
/// Tray configuration: where the server lives and how often to sync. The server
/// URL is resolved in order: the <c>OPD_SERVER_URL</c> environment variable; a
/// non-empty <c>Server:BaseAddress</c> in <c>appsettings.json</c> (written by the
/// EXE installer); then the registry (written by the MSI installer) — an explicit
/// <c>ServerBaseAddress</c>, or derived from the MSI's own filename
/// (<c>OpenPrintDeploy - host.msi</c> → <c>http://host:5080</c>).
/// </summary>
public sealed class TraySettings
{
    private const string RegistryKey = @"SOFTWARE\OpenPrintDeploy\Client";

    // Matches the EXE installer's filename convention (Program.cs).
    private const string FileNameServerDelimiter = " - ";
    private const string FileNameServerScheme = "http";
    private const int FileNameServerPort = 5080;

    public required Uri ServerBaseAddress { get; init; }

    public required TimeSpan SyncInterval { get; init; }

    public static TraySettings Load()
    {
        var url = Environment.GetEnvironmentVariable("OPD_SERVER_URL");
        var intervalMinutes = 5;

        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("Server", out var server))
            {
                if (string.IsNullOrWhiteSpace(url)
                    && server.TryGetProperty("BaseAddress", out var baseAddress))
                {
                    url = baseAddress.GetString();
                }

                if (server.TryGetProperty("SyncIntervalMinutes", out var interval)
                    && interval.TryGetInt32(out var minutes) && minutes > 0)
                {
                    intervalMinutes = minutes;
                }
            }
        }

        // MSI installs configure the server via the registry (the EXE installer
        // writes appsettings.json instead). Honoured only when nothing above set
        // a URL, so the two install paths don't fight.
        if (string.IsNullOrWhiteSpace(url))
        {
            url = ReadServerFromRegistry();
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                "Server base address is not configured (Server:BaseAddress in appsettings.json, " +
                "OPD_SERVER_URL, or the MSI's SERVER property / filename).");
        }

        return new TraySettings
        {
            ServerBaseAddress = new Uri(url, UriKind.Absolute),
            SyncInterval = TimeSpan.FromMinutes(intervalMinutes),
        };
    }

    /// <summary>
    /// The server URL from the MSI-written registry: an explicit
    /// <c>ServerBaseAddress</c> (the <c>SERVER=</c> property), or — failing that —
    /// derived from the installer's own filename via <c>InstallerSource</c>.
    /// </summary>
    private static string? ReadServerFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryKey);
            if (key is null)
            {
                return null;
            }

            if (key.GetValue("ServerBaseAddress") is string explicitUrl
                && !string.IsNullOrWhiteSpace(explicitUrl))
            {
                return explicitUrl.Trim();
            }

            return DeriveServerFromInstallerName(key.GetValue("InstallerSource") as string);
        }
        catch
        {
            // Registry unreadable / key absent — fall through to the not-configured error.
            return null;
        }
    }

    /// <summary>
    /// "…\OpenPrintDeploy - printsrv01.corp.local.msi" → "http://printsrv01.corp.local:5080".
    /// Returns null when the name carries no "<c> - </c>" host segment.
    /// </summary>
    private static string? DeriveServerFromInstallerName(string? installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath))
        {
            return null;
        }

        string name;
        try
        {
            name = Path.GetFileNameWithoutExtension(installerPath);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var idx = name.IndexOf(FileNameServerDelimiter, StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        var host = name[(idx + FileNameServerDelimiter.Length)..].Trim();
        foreach (var ext in new[] { ".msi", ".exe" })
        {
            if (host.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                host = host[..^ext.Length].Trim();
            }
        }

        return host.Length == 0 ? null : $"{FileNameServerScheme}://{host}:{FileNameServerPort}";
    }
}
