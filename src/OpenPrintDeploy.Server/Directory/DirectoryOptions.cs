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
    /// <summary>DC hostname, e.g. <c>dc01.corp.local</c>.</summary>
    public string Server { get; set; } = string.Empty;

    public int Port { get; set; } = 636;

    public bool UseSsl { get; set; } = true;

    /// <summary>Service-account bind DN (read access to user/computer objects).</summary>
    public string BindDn { get; set; } = string.Empty;

    /// <summary>Bind password — supply via user-secrets / protected config, never appsettings.</summary>
    public string BindPassword { get; set; } = string.Empty;

    /// <summary>Search base for locating users and computers, e.g. <c>DC=corp,DC=local</c>.</summary>
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

    /// <summary>Maps a machine name to its OU distinguished name.</summary>
    public Dictionary<string, string> Machines { get; } = new(StringComparer.OrdinalIgnoreCase);
}
