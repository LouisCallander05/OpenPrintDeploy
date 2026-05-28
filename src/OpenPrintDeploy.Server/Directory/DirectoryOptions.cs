namespace OpenPrintDeploy.Server.Directory;

/// <summary>Bound from the <c>Directory</c> configuration section.</summary>
public sealed class DirectoryOptions
{
    public const string SectionName = "Directory";

    /// <summary><c>Ldap</c> for on-prem AD, <c>Stub</c> for local development.</summary>
    public string Provider { get; set; } = "Stub";

    public LdapOptions Ldap { get; set; } = new();

    public StubOptions Stub { get; set; } = new();
}

/// <summary>Connection + bind settings for <c>LdapDirectoryService</c>.</summary>
public sealed class LdapOptions
{
    /// <summary>
    /// How the server authenticates to AD. <c>Negotiate</c> (default) uses the
    /// Windows process identity over Kerberos — the right choice for a
    /// domain-joined server running as Local SYSTEM or a domain service
    /// account, with no password in config. <c>Basic</c> falls back to
    /// <see cref="BindDn"/>+<see cref="BindPassword"/> simple bind for hosts
    /// that aren't domain-joined.
    /// </summary>
    public string AuthMode { get; set; } = "Negotiate";

    /// <summary>
    /// DC hostname, e.g. <c>dc01.corp.local</c>. Leave blank on a domain-joined
    /// server — the service will locate a DC via
    /// <c>Domain.GetCurrentDomain().FindDomainController()</c>.
    /// </summary>
    public string Server { get; set; } = string.Empty;

    public int Port { get; set; } = 636;

    public bool UseSsl { get; set; } = true;

    /// <summary>Bind DN for <c>AuthMode = Basic</c>. Ignored under Negotiate.</summary>
    public string BindDn { get; set; } = string.Empty;

    /// <summary>
    /// Bind password for <c>AuthMode = Basic</c>. Ignored under Negotiate.
    /// Supply via user-secrets / protected config, never appsettings.
    /// </summary>
    public string BindPassword { get; set; } = string.Empty;

    /// <summary>
    /// Search base for users and groups, e.g. <c>DC=corp,DC=local</c>. Leave
    /// blank on a domain-joined server — the service will read it from
    /// <c>Domain.GetCurrentDomain()</c>.
    /// </summary>
    public string SearchBase { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Dev-only escape hatch to skip server certificate validation on LDAPS.
    /// Never enable in production.
    /// </summary>
    public bool AllowInvalidCertificate { get; set; }
}

/// <summary>In-memory directory used when <see cref="DirectoryOptions.Provider"/> is <c>Stub</c>.</summary>
public sealed class StubOptions
{
    /// <summary>Maps a bare username to its group SIDs.</summary>
    public Dictionary<string, List<string>> Users { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps a group's friendly name to its SID, for the zone-rule picker.</summary>
    public Dictionary<string, string> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
}
