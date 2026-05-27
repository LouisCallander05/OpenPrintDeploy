using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;

namespace OpenPrintDeploy.Server.Directory;

/// <summary>
/// Resolves groups and machine OUs from on-prem Active Directory over LDAP.
/// Built on <c>System.DirectoryServices.Protocols</c>, which is cross-platform
/// on .NET 8 (so the type compiles on Linux), though it only runs meaningfully
/// against a reachable domain controller. Every external call is wrapped so a
/// directory outage degrades to "no groups" rather than a 500.
/// </summary>
public sealed class LdapDirectoryService : IDirectoryService
{
    private readonly LdapOptions _ldap;
    private readonly ILogger<LdapDirectoryService> _logger;

    public LdapDirectoryService(IOptions<DirectoryOptions> options, ILogger<LdapDirectoryService> logger)
    {
        _ldap = options.Value.Ldap;
        _logger = logger;
    }

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

    public async Task<string?> GetMachineOuDnAsync(string machineName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(machineName))
        {
            return null;
        }

        try
        {
            return await Task.Run(() => ResolveMachineOu(machineName.Trim()), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LDAP OU resolution failed for {Machine}; OU rules will not match.", machineName);
            return null;
        }
    }

    private IReadOnlySet<string> ResolveGroups(string sam)
    {
        using var connection = CreateBoundConnection();

        var userDn = FindSingleDn(connection, $"(&(objectClass=user)(sAMAccountName={EscapeFilter(sam)}))");
        if (userDn is null)
        {
            _logger.LogWarning("LDAP: user {User} not found under {Base}.", sam, _ldap.SearchBase);
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

    private string? ResolveMachineOu(string machineName)
    {
        using var connection = CreateBoundConnection();

        var dn = FindSingleDn(connection, $"(&(objectClass=computer)(sAMAccountName={EscapeFilter(machineName)}$))");
        if (dn is null)
        {
            return null;
        }

        // Strip the leading RDN (CN=<machine>,) to get the containing OU DN.
        var comma = dn.IndexOf(',', StringComparison.Ordinal);
        return comma >= 0 && comma + 1 < dn.Length ? dn[(comma + 1)..] : null;
    }

    private string? FindSingleDn(LdapConnection connection, string filter)
    {
        var request = new SearchRequest(_ldap.SearchBase, filter, SearchScope.Subtree, "distinguishedName");
        var response = (SearchResponse)connection.SendRequest(request);
        return response.Entries.Count > 0 ? response.Entries[0].DistinguishedName : null;
    }

    private LdapConnection CreateBoundConnection()
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(_ldap.Server, _ldap.Port))
        {
            AuthType = AuthType.Basic,
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

        if (!string.IsNullOrEmpty(_ldap.BindDn))
        {
            connection.Credential = new NetworkCredential(_ldap.BindDn, _ldap.BindPassword);
        }

        connection.Bind();
        return connection;
    }

    private static IReadOnlySet<string> Empty() => new HashSet<string>(StringComparer.Ordinal);

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
