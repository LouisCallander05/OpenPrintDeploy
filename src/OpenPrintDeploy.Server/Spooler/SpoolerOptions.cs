namespace OpenPrintDeploy.Server.Spooler;

/// <summary>Bound from the <c>Spooler</c> configuration section.</summary>
public sealed class SpoolerOptions
{
    public const string SectionName = "Spooler";

    /// <summary>
    /// The hostname clients use to reach this print server. Defaults to the
    /// host's FQDN (<c>HostName.DomainName</c> from
    /// <see cref="IPGlobalProperties"/>) on a domain-joined machine, falling
    /// back to the bare NetBIOS hostname if no DNS domain is set. Override for
    /// cluster names, DNS aliases, or to pin the NetBIOS name explicitly.
    /// </summary>
    public string? ServerName { get; set; }
}
