using System.IO;
using System.Text.Json;
using OpenPrintDeploy.Shared;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>
/// Tray configuration: where the server lives and how often to sync. The server
/// URL is resolved in order: the <c>OPD_SERVER_URL</c> environment variable; a
/// non-empty <c>Server:BaseAddress</c> in <c>appsettings.json</c> (written by the
/// EXE installer); then the registry (written by the MSI installer) — an explicit
/// <c>ServerBaseAddress</c>, or derived from the MSI's own filename
/// (<c>OpenPrintDeploy [server=host] [cert=…].msi</c> → <c>https://host:5443</c>).
/// Any explicit URL is honoured as-is (so an <c>http://</c> server still works);
/// only the filename-derived fallback defaults to HTTPS.
///
/// When the server uses a self-signed certificate, the thumbprint to pin is
/// resolved the same way: <c>OPD_SERVER_CERT_THUMBPRINT</c>,
/// <c>Server:CertificateThumbprint</c>, the registry <c>ServerCertificateThumbprint</c>
/// (the <c>CERTTHUMBPRINT=</c> property), or the <c>[cert=…]</c> token in the
/// installer filename — so a one-click download pins automatically. The tray then
/// trusts exactly that certificate without it being in the machine trust store.
/// </summary>
public sealed class TraySettings
{
    private const string RegistryKey = @"SOFTWARE\OpenPrintDeploy\Client";

    // The filename-derived fallback defaults to HTTPS so a freshly installed
    // client prefers TLS. Host + thumbprint are parsed by InstallerNaming.
    private const string FileNameServerScheme = "https";
    private const int FileNameServerPort = 5443;

    public required Uri ServerBaseAddress { get; init; }

    public required TimeSpan SyncInterval { get; init; }

    /// <summary>
    /// Thumbprint of the server's TLS certificate to pin, or null for normal
    /// chain validation (an operator/CA-issued certificate or one already trusted
    /// by the machine).
    /// </summary>
    public string? ServerCertificateThumbprint { get; init; }

    public static TraySettings Load()
    {
        var url = Environment.GetEnvironmentVariable("OPD_SERVER_URL");
        var thumbprint = Environment.GetEnvironmentVariable("OPD_SERVER_CERT_THUMBPRINT");
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

                if (string.IsNullOrWhiteSpace(thumbprint)
                    && server.TryGetProperty("CertificateThumbprint", out var pin))
                {
                    thumbprint = pin.GetString();
                }

                if (server.TryGetProperty("SyncIntervalMinutes", out var interval)
                    && interval.TryGetInt32(out var minutes) && minutes > 0)
                {
                    intervalMinutes = minutes;
                }
            }
        }

        // MSI installs configure the server via the registry (the EXE installer
        // writes appsettings.json instead). Explicit values win; otherwise both
        // the server and the pinned thumbprint come from the installer's own
        // filename, so a one-click download needs no msiexec properties.
        var identity = InstallerNaming.Parse(ReadRegistryValue("InstallerSource"));

        if (string.IsNullOrWhiteSpace(url))
        {
            url = ReadRegistryValue("ServerBaseAddress"); // explicit SERVER=
            if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(identity.Host))
            {
                url = $"{FileNameServerScheme}://{identity.Host}:{FileNameServerPort}";
            }
        }

        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            thumbprint = ReadRegistryValue("ServerCertificateThumbprint"); // explicit CERTTHUMBPRINT=
            if (string.IsNullOrWhiteSpace(thumbprint))
            {
                thumbprint = identity.Thumbprint;
            }
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
            ServerCertificateThumbprint = string.IsNullOrWhiteSpace(thumbprint) ? null : thumbprint.Trim(),
        };
    }

    /// <summary>Reads a single string value from the client registry key, or null if absent/unreadable.</summary>
    private static string? ReadRegistryValue(string name)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryKey);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }
}
