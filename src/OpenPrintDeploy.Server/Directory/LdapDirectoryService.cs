using System.DirectoryServices.ActiveDirectory;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;

namespace OpenPrintDeploy.Server.Directory;

/// <summary>
/// Resolves groups from on-prem Active Directory over LDAP. Built on
/// <c>System.DirectoryServices.Protocols</c>, with optional Windows-only
/// auto-discovery of the domain controller and search base via
/// <c>Domain.GetCurrentDomain()</c> when the server is domain-joined and
/// those options are left blank. Every external call is wrapped so a
/// directory outage degrades to "no groups" rather than a 500.
/// </summary>
public sealed class LdapDirectoryService : IDirectoryService
{
    private readonly LdapOptions _ldap;
    private readonly ILogger<LdapDirectoryService> _logger;
    private readonly Lazy<Endpoint> _endpoint;

    public LdapDirectoryService(IOptions<DirectoryOptions> options, ILogger<LdapDirectoryService> logger)
    {
        _ldap = options.Value.Ldap;
        _logger = logger;
        _endpoint = new Lazy<Endpoint>(ResolveEndpoint, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Resolved DC hostname + search-base DN, after auto-discovery.</summary>
    private sealed record Endpoint(string Server, string SearchBase);

    public async Task<IReadOnlySet<string>> GetGroupSidsAsync(string username, CancellationToken ct = default)
    {
        var sam = DirectoryUsername.Normalize(username);
        if (string.IsNullOrEmpty(sam))
        {
            return Empty();
        }

        try
        {
            return await Task.Run(() => ResolveGroups(sam), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LDAP group resolution failed for {User}; treating as no groups.", sam);
            return Empty();
        }
    }

    public async Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(
        string query, int limit, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() => SearchGroups(query?.Trim() ?? string.Empty, limit), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LDAP group search failed; admin falls back to raw-SID entry.");
            return [];
        }
    }

    public async Task<string?> ResolveGroupNameAsync(string sid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            return null;
        }

        try
        {
            return await Task.Run(() => ResolveGroupName(sid.Trim()), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LDAP name resolution failed for {Sid}; showing the raw SID.", sid);
            return null;
        }
    }

    public async Task<DirectoryDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        return await Task.Run(() => RunDiagnostics(), ct);
    }

    private DirectoryDiagnostics RunDiagnostics()
    {
        Endpoint endpoint;
        try
        {
            endpoint = _endpoint.Value;
        }
        catch (Exception ex)
        {
            return new DirectoryDiagnostics(
                Provider: "Ldap",
                AuthMode: _ldap.AuthMode,
                Server: NullIfBlank(_ldap.Server),
                SearchBase: NullIfBlank(_ldap.SearchBase),
                Connected: false,
                SampleGroupCount: null,
                Error: $"Endpoint discovery failed: {ex.Message}");
        }

        try
        {
            using var connection = CreateBoundConnection(endpoint);
            var sample = SearchGroupsCore(connection, endpoint, query: string.Empty, limit: 5).Count;
            return new DirectoryDiagnostics(
                Provider: "Ldap",
                AuthMode: _ldap.AuthMode,
                Server: endpoint.Server,
                SearchBase: endpoint.SearchBase,
                Connected: true,
                SampleGroupCount: sample,
                Error: null);
        }
        catch (Exception ex)
        {
            return new DirectoryDiagnostics(
                Provider: "Ldap",
                AuthMode: _ldap.AuthMode,
                Server: endpoint.Server,
                SearchBase: endpoint.SearchBase,
                Connected: false,
                SampleGroupCount: null,
                Error: ex.Message);
        }
    }

    private IReadOnlyList<DirectoryGroup> SearchGroups(string query, int limit)
    {
        var endpoint = _endpoint.Value;
        using var connection = CreateBoundConnection(endpoint);
        return SearchGroupsCore(connection, endpoint, query, limit);
    }

    private static List<DirectoryGroup> SearchGroupsCore(
        LdapConnection connection, Endpoint endpoint, string query, int limit)
    {
        // Empty query lists the first groups; otherwise substring-match on the
        // common name or the down-level (sAMAccountName) name.
        var filter = query.Length == 0
            ? "(objectClass=group)"
            : $"(&(objectClass=group)(|(cn=*{EscapeFilter(query)}*)(sAMAccountName=*{EscapeFilter(query)}*)))";

        var request = new SearchRequest(endpoint.SearchBase, filter, SearchScope.Subtree, "sAMAccountName", "cn", "objectSid")
        {
            SizeLimit = Math.Max(0, limit),
        };

        // Sort by cn ascending server-side so when the result set is larger
        // than SizeLimit, the slice we get back is the alphabetically-earliest
        // matches — not whatever insertion order AD happens to return. That
        // makes "type any prefix of the group name and see it" actually work.
        request.Controls.Add(new SortRequestControl(
            new System.DirectoryServices.Protocols.SortKey("cn", null, reverseOrder: false)));

        SearchResponse response;
        try
        {
            response = (SearchResponse)connection.SendRequest(request);
        }
        catch (DirectoryOperationException ex) when (ex.Response is SearchResponse partial)
        {
            // A SizeLimitExceeded result still carries the capped entries.
            response = partial;
        }

        var groups = new List<DirectoryGroup>(response.Entries.Count);
        foreach (SearchResultEntry entry in response.Entries)
        {
            if (entry.Attributes["objectSid"]?.GetValues(typeof(byte[])).FirstOrDefault() is byte[] raw)
            {
                groups.Add(new DirectoryGroup(SidConverter.ToSidString(raw), GroupName(entry)));
            }
        }

        // Already alphabetical from the server; the client-side OrderBy is a
        // belt-and-braces sort in case the SortRequestControl isn't honoured.
        return groups
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string? ResolveGroupName(string sid)
    {
        var endpoint = _endpoint.Value;
        using var connection = CreateBoundConnection(endpoint);

        var filter = $"(&(objectClass=group)(objectSid={EscapeBinary(SidConverter.FromSidString(sid))}))";
        var request = new SearchRequest(endpoint.SearchBase, filter, SearchScope.Subtree, "sAMAccountName", "cn");
        var response = (SearchResponse)connection.SendRequest(request);

        return response.Entries.Count > 0 ? GroupName(response.Entries[0]) : null;
    }

    private IReadOnlySet<string> ResolveGroups(string sam)
    {
        var endpoint = _endpoint.Value;
        using var connection = CreateBoundConnection(endpoint);

        var userDn = FindSingleDn(connection, endpoint.SearchBase,
            $"(&(objectClass=user)(sAMAccountName={EscapeFilter(sam)}))");
        if (userDn is null)
        {
            _logger.LogWarning("LDAP: user {User} not found under {Base}.", sam, endpoint.SearchBase);
            return Empty();
        }

        // tokenGroups is a constructed attribute: it's only computed on a
        // Base-scope read of the user's own DN, and already contains the
        // transitive (nested + primary) group SIDs.
        var request = new SearchRequest(userDn, "(objectClass=*)", SearchScope.Base, "tokenGroups");
        var response = (SearchResponse)connection.SendRequest(request);

        var sids = new HashSet<string>(StringComparer.Ordinal);
        if (response.Entries.Count == 0)
        {
            return sids;
        }

        var attribute = response.Entries[0].Attributes["tokenGroups"];
        if (attribute is null)
        {
            return sids;
        }

        foreach (var value in attribute.GetValues(typeof(byte[])))
        {
            if (value is byte[] raw)
            {
                sids.Add(SidConverter.ToSidString(raw));
            }
        }

        return sids;
    }

    private static string? FindSingleDn(LdapConnection connection, string searchBase, string filter)
    {
        var request = new SearchRequest(searchBase, filter, SearchScope.Subtree, "distinguishedName");
        var response = (SearchResponse)connection.SendRequest(request);
        return response.Entries.Count > 0 ? response.Entries[0].DistinguishedName : null;
    }

    private LdapConnection CreateBoundConnection(Endpoint endpoint)
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(endpoint.Server, _ldap.Port))
        {
            Timeout = TimeSpan.FromSeconds(_ldap.TimeoutSeconds),
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

        if (_ldap.UseSsl)
        {
            connection.SessionOptions.SecureSocketLayer = true;
            if (_ldap.AllowInvalidCertificate)
            {
                connection.SessionOptions.VerifyServerCertificate = (_, _) => true;
            }
        }

        if (IsNegotiate(_ldap.AuthMode))
        {
            // Process-identity bind via Kerberos/SSPI. On a domain-joined host
            // running as Local SYSTEM or a domain service account, no password
            // is stored anywhere — AD authenticates the running service.
            connection.AuthType = AuthType.Negotiate;
            connection.Bind(CredentialCache.DefaultNetworkCredentials);
        }
        else
        {
            connection.AuthType = AuthType.Basic;
            if (!string.IsNullOrEmpty(_ldap.BindDn))
            {
                connection.Credential = new NetworkCredential(_ldap.BindDn, _ldap.BindPassword);
            }
            connection.Bind();
        }

        return connection;
    }

    private Endpoint ResolveEndpoint()
    {
        var server = string.IsNullOrWhiteSpace(_ldap.Server)
            ? DiscoverDc()
            : _ldap.Server.Trim();

        var searchBase = string.IsNullOrWhiteSpace(_ldap.SearchBase)
            ? DiscoverSearchBase()
            : _ldap.SearchBase.Trim();

        if (_ldap.Server != server || _ldap.SearchBase != searchBase)
        {
            _logger.LogInformation(
                "LDAP endpoint resolved: Server={Server} SearchBase={Base} (auto-discovered fields blank in config)",
                server, searchBase);
        }

        return new Endpoint(server, searchBase);
    }

    private static string DiscoverDc()
    {
        if (OperatingSystem.IsWindows())
        {
            using var domain = Domain.GetCurrentDomain();
            return domain.FindDomainController().Name;
        }
        throw new InvalidOperationException(
            "LDAP auto-discovery requires Windows. Set Directory:Ldap:Server explicitly on this host.");
    }

    private static string DiscoverSearchBase()
    {
        if (OperatingSystem.IsWindows())
        {
            using var domain = Domain.GetCurrentDomain();
            using var entry = domain.GetDirectoryEntry();
            var dn = entry.Properties["distinguishedName"].Value as string
                ?? throw new InvalidOperationException("Domain DN attribute was empty.");
            return dn;
        }
        throw new InvalidOperationException(
            "LDAP auto-discovery requires Windows. Set Directory:Ldap:SearchBase explicitly on this host.");
    }

    private static bool IsNegotiate(string? mode)
        => string.IsNullOrWhiteSpace(mode)
            || mode.Equals("Negotiate", StringComparison.OrdinalIgnoreCase);

    /// <summary>Prefers the down-level name, falling back to the common name then the DN.</summary>
    private static string GroupName(SearchResultEntry entry)
        => StringAttr(entry, "sAMAccountName")
            ?? StringAttr(entry, "cn")
            ?? entry.DistinguishedName;

    private static string? StringAttr(SearchResultEntry entry, string name)
        => entry.Attributes[name]?.GetValues(typeof(string)).FirstOrDefault() as string;

    /// <summary>Encodes raw bytes as an LDAP filter assertion value (<c>\XX</c> per byte).</summary>
    private static string EscapeBinary(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            builder.Append('\\').Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static IReadOnlySet<string> Empty() => new HashSet<string>(StringComparer.Ordinal);

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Escapes an LDAP search-filter assertion value per RFC 4515.</summary>
    private static string EscapeFilter(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '\\' => "\\5c",
                '*' => "\\2a",
                '(' => "\\28",
                ')' => "\\29",
                '\0' => "\\00",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }
}
