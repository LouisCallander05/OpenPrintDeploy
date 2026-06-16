using System.Collections.Concurrent;
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

    // This service is a singleton, so these caches live for the process. Each
    // LDAP lookup opens a fresh bound connection (a Kerberos handshake), so the
    // admin UI — which resolves the same group SIDs and re-fetches the same
    // catalog on every Zones page load — pays that cost repeatedly without them.
    //
    // SID -> friendly name. Group names change rarely; a generous TTL keeps the
    // zones table and rule labels instant after the first resolve.
    private readonly ConcurrentDictionary<string, CacheEntry<string>> _nameCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan NameCacheTtl = TimeSpan.FromMinutes(10);

    // The empty-query catalog (the first-N groups seeding the rule picker). A
    // short TTL absorbs repeat page loads and the prerender+interactive
    // double-render without going stale on real directory changes for long.
    private readonly object _catalogLock = new();
    private List<DirectoryGroup>? _catalog;
    private int _catalogLimit;
    private DateTime _catalogExpiresUtc;
    private static readonly TimeSpan CatalogCacheTtl = TimeSpan.FromSeconds(60);

    // Forest-wide endpoints (one per domain) for cross-domain resolution. A user
    // or admin group can live in a different domain than this server; we resolve
    // those per-domain over the existing LDAP path. Discovered once and cached.
    private readonly Lazy<IReadOnlyList<Endpoint>> _forestEndpoints;

    // Group NAME -> SID, populated by ResolveGroupSidByNameAsync. The admin
    // authorization path resolves the same configured group names on every
    // request, so a TTL keeps that fast after the first lookup.
    private readonly ConcurrentDictionary<string, CacheEntry<string>> _sidByNameCache =
        new(StringComparer.OrdinalIgnoreCase);

    public LdapDirectoryService(IOptions<DirectoryOptions> options, ILogger<LdapDirectoryService> logger)
    {
        _ldap = options.Value.Ldap;
        _logger = logger;
        _endpoint = new Lazy<Endpoint>(ResolveEndpoint, LazyThreadSafetyMode.ExecutionAndPublication);
        _forestEndpoints = new Lazy<IReadOnlyList<Endpoint>>(
            ResolveForestEndpoints, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Resolved DC hostname + search-base DN, after auto-discovery.</summary>
    private sealed record Endpoint(string Server, string SearchBase);

    private readonly record struct CacheEntry<T>(T Value, DateTime ExpiresUtc);

    public async Task<GroupResolution> GetGroupSidsAsync(string username, CancellationToken ct = default)
    {
        var sam = DirectoryUsername.Normalize(username);
        if (string.IsNullOrEmpty(sam))
        {
            // No account to resolve — an authoritative "no groups", not an outage.
            return GroupResolution.Resolved(Empty());
        }

        var domainHint = DirectoryUsername.ExtractDomain(username);

        try
        {
            return await Task.Run(() =>
            {
                // Track whether ANY directory endpoint actually answered. If every
                // attempt failed to connect/bind, we cannot tell "no groups" from
                // "directory down", so we must report unavailable and let the
                // client keep its current printers.
                var reachable = false;

                // 1. Explicit domain prefix (e.g. edu002\ST02438): route straight
                //    to that DC so cross-domain lookups don't fall through the
                //    home-domain search first.
                if (domainHint is not null && FindEndpointForDomain(domainHint) is { } target)
                {
                    var hit = ResolveGroupsOnEndpoint(sam, target);
                    reachable |= hit.Reachable;
                    if (hit.Sids.Count > 0)
                    {
                        return GroupResolution.Resolved(hit.Sids);
                    }
                }

                // 2. This server's own domain.
                var home = ResolveGroupsOnEndpoint(sam, _endpoint.Value);
                reachable |= home.Reachable;
                if (home.Sids.Count > 0)
                {
                    return GroupResolution.Resolved(home.Sids);
                }

                // 3. Forest-wide — the user may live in another domain (edu002 vs
                //    edu001). Each domain is best-effort.
                foreach (var endpoint in _forestEndpoints.Value)
                {
                    var hit = ResolveGroupsOnEndpoint(sam, endpoint);
                    reachable |= hit.Reachable;
                    if (hit.Sids.Count > 0)
                    {
                        return GroupResolution.Resolved(hit.Sids);
                    }
                }

                // Nothing found anywhere. If we reached at least one DC this is an
                // authoritative "no groups"; if every DC was unreachable it's an
                // outage.
                if (reachable)
                {
                    return GroupResolution.Resolved(Empty());
                }

                _logger.LogWarning(
                    "LDAP group resolution for {User}: no directory endpoint was reachable; reporting unavailable.", sam);
                return GroupResolution.Unavailable;
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LDAP group resolution failed for {User}; treating as directory unavailable.", sam);
            return GroupResolution.Unavailable;
        }
    }

    public async Task<string?> ResolveGroupSidByNameAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var key = name.Trim();
        if (TryGetCachedSid(key, out var cached))
        {
            return cached;
        }

        try
        {
            var sid = await Task.Run(() => ResolveGroupSidForestWide(key), ct);
            if (sid is not null)
            {
                CacheSid(key, sid);
            }

            return sid;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Forest group-SID resolution failed for {Name}.", name);
            return null;
        }
    }

    public async Task<bool> ValidateCredentialsAsync(string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        try
        {
            return await Task.Run(() => TryBindAsUser(username.Trim(), password), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LDAP credential validation errored for {User}; denying.", username);
            return false;
        }
    }

    /// <summary>
    /// Binds to the directory AS the supplied user. A successful bind means the
    /// password is valid; an <see cref="LdapException"/> means it isn't (or the
    /// account is disabled/locked). The username may be <c>DOMAIN\user</c> or a
    /// <c>user@domain</c> UPN — Negotiate handles both.
    /// </summary>
    private bool TryBindAsUser(string username, string password)
    {
        var endpoint = _endpoint.Value;
        using var connection = new LdapConnection(new LdapDirectoryIdentifier(endpoint.Server, _ldap.Port))
        {
            Timeout = TimeSpan.FromSeconds(_ldap.TimeoutSeconds),
            AuthType = AuthType.Negotiate,
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

        try
        {
            connection.Bind(new NetworkCredential(username, password));
            return true;
        }
        catch (LdapException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(
        string query, int limit, CancellationToken ct = default)
    {
        var trimmed = query?.Trim() ?? string.Empty;

        // Empty-query catalog fetches are the per-page-load cost; serve a fresh
        // enough cached copy when one is available.
        if (trimmed.Length == 0 && TryGetCatalog(limit, out var cached))
        {
            return cached;
        }

        try
        {
            var results = await Task.Run(() => SearchGroups(trimmed, limit), ct);

            // Every group we just listed is a free SID->name resolution for the
            // zones table and rule labels — warm the cache with them.
            foreach (var group in results)
            {
                CacheName(group.Sid, group.Name);
            }

            if (trimmed.Length == 0)
            {
                StoreCatalog(limit, results);
            }

            return results;
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

        var key = sid.Trim();
        if (TryGetCachedName(key, out var name))
        {
            return name;
        }

        try
        {
            var resolved = await Task.Run(() => ResolveGroupName(key), ct);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                CacheName(key, resolved!);
            }

            return resolved;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LDAP name resolution failed for {Sid}; showing the raw SID.", sid);
            return null;
        }
    }

    private bool TryGetCachedName(string sid, out string? name)
    {
        if (_nameCache.TryGetValue(sid, out var entry) && entry.ExpiresUtc > DateTime.UtcNow)
        {
            name = entry.Value;
            return true;
        }

        name = null;
        return false;
    }

    private void CacheName(string sid, string name)
        => _nameCache[sid] = new CacheEntry<string>(name, DateTime.UtcNow + NameCacheTtl);

    private bool TryGetCachedSid(string name, out string? sid)
    {
        if (_sidByNameCache.TryGetValue(name, out var entry) && entry.ExpiresUtc > DateTime.UtcNow)
        {
            sid = entry.Value;
            return true;
        }

        sid = null;
        return false;
    }

    private void CacheSid(string name, string sid)
        => _sidByNameCache[name] = new CacheEntry<string>(sid, DateTime.UtcNow + NameCacheTtl);

    private bool TryGetCatalog(int limit, out IReadOnlyList<DirectoryGroup> catalog)
    {
        lock (_catalogLock)
        {
            if (_catalog is not null && _catalogLimit == limit && _catalogExpiresUtc > DateTime.UtcNow)
            {
                catalog = _catalog;
                return true;
            }
        }

        catalog = [];
        return false;
    }

    private void StoreCatalog(int limit, IReadOnlyList<DirectoryGroup> results)
    {
        lock (_catalogLock)
        {
            _catalog = results.ToList();
            _catalogLimit = limit;
            _catalogExpiresUtc = DateTime.UtcNow + CatalogCacheTtl;
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
        // Two-pass search to make typeahead useful in real ADs:
        //   Pass 1 — prefix match (cn=q* / sAMAccountName=q*). With structured
        //            group names like "0912-ls-All Staff" this is what the
        //            admin almost always wants, and the result set is small.
        //   Pass 2 — substring fallback (cn=*q*) only if the prefix pass
        //            didn't fill the window. So a query that only matches
        //            something in the middle of the name (e.g. "All Staff")
        //            still works.
        // The two passes are returned as ORDERED sections: prefix block first
        // (alphabetical), then substring block (alphabetical). Crucially we
        // don't re-sort the merged list — doing so would let alphabetical
        // substring hits like "0001-gs-Students-Year 09" leapfrog the prefix
        // matches like "0912-..." that the admin actually typed for.
        var prefixMatches = new List<DirectoryGroup>();
        if (query.Length > 0)
        {
            var prefixFilter = $"(&(objectClass=group)(|(cn={EscapeFilter(query)}*)(sAMAccountName={EscapeFilter(query)}*)))";
            prefixMatches.AddRange(ExecuteSearch(connection, endpoint, prefixFilter, limit));
        }

        // Substring fallback runs only when prefix returned NOTHING — the
        // common typeahead case (structured group names) finishes in a single
        // LDAP query that way. The fallback still covers the rare "match the
        // middle of the name" case (e.g. typing "All Staff" with no "All*"
        // groups in AD).
        var substringMatches = new List<DirectoryGroup>();
        if (prefixMatches.Count == 0)
        {
            var fallback = query.Length == 0
                ? "(objectClass=group)"
                : $"(&(objectClass=group)(|(cn=*{EscapeFilter(query)}*)(sAMAccountName=*{EscapeFilter(query)}*)))";
            substringMatches.AddRange(ExecuteSearch(connection, endpoint, fallback, limit));
        }

        // Belt-and-braces alphabetical in case the server ignored our
        // SortRequestControl. Prefix block before substring block — never
        // re-sort the merged list.
        return prefixMatches
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Concat(substringMatches.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Runs one filtered group search with the server sorting alphabetically by
    /// <c>cn</c>. SizeLimitExceeded is treated as success with the capped slice
    /// — that's what gives us a useful first-N when the directory is huge.
    /// </summary>
    private static List<DirectoryGroup> ExecuteSearch(
        LdapConnection connection, Endpoint endpoint, string filter, int limit)
    {
        var request = new SearchRequest(endpoint.SearchBase, filter, SearchScope.Subtree, "sAMAccountName", "cn", "objectSid")
        {
            SizeLimit = Math.Max(0, limit),
        };
        request.Controls.Add(new SortRequestControl(
            new System.DirectoryServices.Protocols.SortKey("cn", null, reverseOrder: false)));

        SearchResponse response;
        try
        {
            response = (SearchResponse)connection.SendRequest(request);
        }
        catch (DirectoryOperationException ex) when (ex.Response is SearchResponse partial)
        {
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
        return groups;
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

    /// <summary>The SIDs read from one endpoint, plus whether that endpoint actually answered.</summary>
    private readonly record struct EndpointGroups(IReadOnlySet<string> Sids, bool Reachable);

    /// <summary>
    /// Reads tokenGroups for <paramref name="sam"/> from a specific DC endpoint.
    /// Never throws, so callers can safely iterate multiple endpoints.
    /// <see cref="EndpointGroups.Reachable"/> distinguishes "the DC answered but
    /// the user/groups aren't here" (true, empty set) from "the DC could not be
    /// reached at all" (false) — the latter must not be read as "no groups".
    /// </summary>
    private EndpointGroups ResolveGroupsOnEndpoint(string sam, Endpoint endpoint)
    {
        try
        {
            using var connection = CreateBoundConnection(endpoint);
            var userDn = FindSingleDn(connection, endpoint.SearchBase,
                $"(&(objectClass=user)(sAMAccountName={EscapeFilter(sam)}))");
            if (userDn is null)
            {
                // The DC answered — the user simply isn't in this domain.
                return new EndpointGroups(Empty(), Reachable: true);
            }

            // tokenGroups is a constructed attribute: it's only computed on a
            // Base-scope read of the user's own DN, and already contains the
            // transitive (nested + primary) group SIDs.
            var request = new SearchRequest(userDn, "(objectClass=*)", SearchScope.Base, "tokenGroups");
            var response = (SearchResponse)connection.SendRequest(request);

            var sids = new HashSet<string>(StringComparer.Ordinal);
            if (response.Entries.Count > 0
                && response.Entries[0].Attributes["tokenGroups"] is { } attribute)
            {
                foreach (var value in attribute.GetValues(typeof(byte[])))
                {
                    if (value is byte[] raw)
                    {
                        sids.Add(SidConverter.ToSidString(raw));
                    }
                }
            }

            return new EndpointGroups(sids, Reachable: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "User lookup failed in {Domain}.", endpoint.Server);
            return new EndpointGroups(Empty(), Reachable: false);
        }
    }

    /// <summary>
    /// Finds the forest endpoint whose DNS name matches a domain hint. Accepts
    /// a NetBIOS-style prefix (<c>edu002</c> matches <c>edu002.services.vic.gov.au</c>)
    /// or an exact DNS name. Returns null if no endpoint matches.
    /// </summary>
    private Endpoint? FindEndpointForDomain(string domainHint)
    {
        var hint = domainHint.Trim();
        foreach (var ep in _forestEndpoints.Value)
        {
            if (ep.Server.Equals(hint, StringComparison.OrdinalIgnoreCase)
                || ep.Server.StartsWith(hint + ".", StringComparison.OrdinalIgnoreCase))
            {
                return ep;
            }
        }

        return null;
    }

    /// <summary>Searches every forest domain for a group by name, returning the first SID match.</summary>
    private string? ResolveGroupSidForestWide(string name)
    {
        foreach (var endpoint in _forestEndpoints.Value)
        {
            try
            {
                using var connection = CreateBoundConnection(endpoint);
                var filter =
                    $"(&(objectClass=group)(|(sAMAccountName={EscapeFilter(name)})(cn={EscapeFilter(name)})))";
                var request = new SearchRequest(
                    endpoint.SearchBase, filter, SearchScope.Subtree, "sAMAccountName", "cn", "objectSid")
                {
                    SizeLimit = 5,
                };

                SearchResponse response;
                try
                {
                    response = (SearchResponse)connection.SendRequest(request);
                }
                catch (DirectoryOperationException ex) when (ex.Response is SearchResponse partial)
                {
                    response = partial;
                }

                foreach (SearchResultEntry entry in response.Entries)
                {
                    if (GroupName(entry).Equals(name, StringComparison.OrdinalIgnoreCase)
                        && entry.Attributes["objectSid"]?.GetValues(typeof(byte[])).FirstOrDefault() is byte[] raw)
                    {
                        return SidConverter.ToSidString(raw);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Forest group search failed in {Domain}.", endpoint.Server);
            }
        }

        return null;
    }

    /// <summary>
    /// One <see cref="Endpoint"/> per domain in the forest (DNS name + DN). Falls
    /// back to just the primary endpoint when the forest can't be enumerated
    /// (non-Windows, not domain-joined, or no permission).
    /// </summary>
    private IReadOnlyList<Endpoint> ResolveForestEndpoints()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var forest = Forest.GetCurrentForest();
                var endpoints = new List<Endpoint>();
                foreach (Domain domain in forest.Domains)
                {
                    endpoints.Add(new Endpoint(domain.Name, DomainDnsToDn(domain.Name)));
                }

                if (endpoints.Count > 0)
                {
                    _logger.LogInformation("Forest domains for cross-domain resolution: {Domains}.",
                        string.Join(", ", endpoints.Select(e => e.Server)));
                    return endpoints;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Forest discovery failed; cross-domain resolution falls back to this domain.");
        }

        return [_endpoint.Value];
    }

    /// <summary>"edu002.services.vic.gov.au" -&gt; "DC=edu002,DC=services,DC=vic,DC=gov,DC=au".</summary>
    private static string DomainDnsToDn(string dns)
        => string.Join(",", dns.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(part => $"DC={part}"));

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
