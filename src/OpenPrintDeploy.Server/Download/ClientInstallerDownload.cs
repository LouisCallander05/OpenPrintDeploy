using System.Net.NetworkInformation;

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
    // Where Publish-Server.ps1 places the single-file client installer, relative
    // to the server's content root. Overridable via Client:InstallerPath.
    public const string DefaultRelativePath = "client/OpenPrintDeploy.Client.Installer.exe";

    /// <summary>Absolute path to the installer the server hands out.</summary>
    public static string ResolveInstallerPath(IConfiguration cfg, string contentRoot)
    {
        var configured = cfg["Client:InstallerPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(contentRoot, configured);
        }

        return Path.Combine(contentRoot, DefaultRelativePath);
    }

    /// <summary>
    /// Filename offered to the browser: <c>OpenPrintDeploy - &lt;host&gt;.exe</c>.
    /// The installer turns <c>&lt;host&gt;</c> into <c>http://&lt;host&gt;:5080</c>,
    /// so this assumes the server is reachable over http on 5080 (the shipped
    /// default — see appsettings "Urls"). Operators on a different scheme/port
    /// should hand out the bare installer and pass <c>--server</c> explicitly.
    /// </summary>
    public static string DownloadFileName(IConfiguration cfg)
        => $"OpenPrintDeploy - {ResolveHost(cfg)}.exe";

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
