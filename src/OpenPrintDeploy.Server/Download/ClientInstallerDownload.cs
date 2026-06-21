using System.Net.NetworkInformation;
using OpenPrintDeploy.Shared;

namespace OpenPrintDeploy.Server.Download;

/// <summary>
/// Locates the bundled tray-client installer and works out the download
/// filename so the file an admin saves is <c>OpenPrintDeploy - &lt;host&gt;.exe</c>.
/// The installer reads that host out of its own filename and configures itself
/// to talk to this server — zero arguments, Intune-friendly. Publish-Server.ps1
/// drops the single-file installer under <c>client/</c> next to the server exe.
/// </summary>
public static class ClientInstallerDownload
{
    // Where Publish-Server.ps1 places the client MSI, relative to the server's
    // content root. Overridable via Client:MsiPath.
    public const string DefaultMsiRelativePath = "client/OpenPrintDeploy.Client.msi";

    /// <summary>Absolute path to the client MSI the server hands out.</summary>
    public static string ResolveMsiPath(IConfiguration cfg, string contentRoot)
    {
        var configured = cfg["Client:MsiPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(contentRoot, configured);
        }

        return Path.Combine(contentRoot, DefaultMsiRelativePath);
    }

    /// <summary>
    /// Filename offered to the browser for the MSI:
    /// <c>OpenPrintDeploy - &lt;host&gt; - &lt;thumbprint&gt;.msi</c> (thumbprint segment
    /// omitted when null). The tray reads the host (and, for a self-signed server,
    /// the certificate thumbprint to pin) out of this filename — recorded by the
    /// MSI as <c>OriginalDatabase</c> — so a one-click download + double-click
    /// installs a correctly targeted, certificate-pinned client. The " - "
    /// separators avoid glob characters that break packaging tools; for Intune,
    /// rename to a plain name and pass SERVER=/CERTTHUMBPRINT= instead. Pass the
    /// thumbprint only for a self-signed cert (a CA cert needs no pin).
    /// </summary>
    public static string MsiDownloadFileName(IConfiguration cfg, string? pinnedThumbprint = null)
        => InstallerNaming.Compose(ResolveHost(cfg), pinnedThumbprint);

    /// <summary>
    /// The host clients should reach this server on. Explicit
    /// <c>Spooler:ServerName</c> wins (it's the same identity baked into printer
    /// UNCs, so the two stay consistent); otherwise the domain-joined FQDN;
    /// otherwise the bare machine name.
    /// </summary>
    public static string ResolveHost(IConfiguration cfg)
    {
        var configured = cfg["Spooler:ServerName"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            var host = string.IsNullOrWhiteSpace(props.HostName) ? Environment.MachineName : props.HostName;
            var domain = props.DomainName?.Trim();
            return string.IsNullOrWhiteSpace(domain) ? host : $"{host}.{domain}";
        }
        catch
        {
            return Environment.MachineName;
        }
    }
}
