using Microsoft.Win32;
using System.Text.Json;
using OpenPrintDeploy.Client.Core;
using OpenPrintDeploy.Shared;

namespace OpenPrintDeploy.Client.Service;

internal sealed record MachineSettings(Uri Server, string? Pin, IReadOnlyList<string> Hosts, bool Enabled)
{
    public static MachineSettings Load()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(ClientPolicy.RegistrySubKey);
        string? Read(string name) => key?.GetValue(name)?.ToString();
        var identity = InstallerNaming.Parse(Read("InstallerSource"));
        string? url = null;
        string? pin = null;
        string? hosts = null;
        // Only the protected install directory and HKLM configure this service.
        // In particular, never use tray environment variables or HKCU values.
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("Server", out var section))
            {
                string? Value(string name) => section.TryGetProperty(name, out var value) ? value.GetString() : null;
                if (string.IsNullOrWhiteSpace(url)) url = Value("BaseAddress");
                if (string.IsNullOrWhiteSpace(pin)) pin = Value("CertificateThumbprint");
                if (string.IsNullOrWhiteSpace(hosts)) hosts = Value("AllowedPrintServers");
            }
        }
        // Match the tray's machine-source precedence (EXE settings, then MSI
        // registry); user environment overrides are intentionally excluded.
        if (string.IsNullOrWhiteSpace(url)) url = Read("ServerBaseAddress");
        if (string.IsNullOrWhiteSpace(pin)) pin = Read("ServerCertificateThumbprint");
        if (string.IsNullOrWhiteSpace(hosts)) hosts = Read("AllowedPrintServers");
        if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(identity.Host)) url = $"https://{identity.Host}:5443";
        if (string.IsNullOrWhiteSpace(pin)) pin = identity.Thumbprint;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var server) || server.Scheme != "https"
            || !string.IsNullOrEmpty(server.UserInfo) || server.AbsolutePath != "/" || !string.IsNullOrEmpty(server.Query)
            || !string.IsNullOrEmpty(server.Fragment))
            throw new InvalidOperationException("Driver service requires an administrator-configured HTTPS server origin.");
        IReadOnlyList<string> allowed = string.IsNullOrWhiteSpace(hosts) ? [server.Host]
            : hosts.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new(server, pin, allowed, Read("DriverUpdatesEnabled") != "0");
    }
}
