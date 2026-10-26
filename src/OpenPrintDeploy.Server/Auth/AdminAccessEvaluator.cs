using System.Security.Claims;
using Microsoft.Extensions.Options;
using OpenPrintDeploy.Server.Directory;

namespace OpenPrintDeploy.Server.Auth;

/// <summary>
/// Decides whether a principal is an admin, from the effective grant set
/// (appsettings break-glass ∪ the Settings-page store). Group grants are by
/// NAME and resolved to SIDs via the directory; user grants match the
/// full authenticated account name or primary user SID. Shared by authorization and the Settings
/// page (which uses it to refuse a save that would lock the current admin out).
/// </summary>
public sealed class AdminAccessEvaluator
{
    private readonly IDirectoryService _directory;
    private readonly AdminAccessStore _store;
    private readonly AdminAuthOptions _configured;
    private readonly bool _development;

    public AdminAccessEvaluator(IDirectoryService directory, AdminAccessStore store, IOptions<AuthOptions> options,
        IHostEnvironment? environment = null)
    {
        _directory = directory;
        _store = store;
        _configured = options.Value.Admin;
        _development = environment?.IsDevelopment() == true;
    }

    /// <summary>The persisted (editable) grants only.</summary>
    public AdminAccess GetStored() => _store.Load();

    public void SaveStored(AdminAccess access) => _store.Save(access);

    /// <summary>Combines the appsettings grants with the supplied stored grants.</summary>
    public EffectiveAdminAccess Combine(AdminAccess stored, bool storeSealed = false)
    {
        var groups = _configured.Groups.Concat(stored.Groups)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var users = _configured.Users.Concat(stored.Users)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sids = _configured.GroupSids.Distinct(StringComparer.Ordinal).ToList();
        return new EffectiveAdminAccess(groups, users, sids, Sealed: storeSealed);
    }

    public EffectiveAdminAccess GetEffective()
    {
        var load = _store.Read();
        return Combine(load.Access, load.Unreadable);
    }

    public Task<bool> IsAdminAsync(ClaimsPrincipal user, CancellationToken ct = default)
        => IsAdminAsync(user, GetEffective(), ct);

    public async Task<bool> IsAdminAsync(
        ClaimsPrincipal user, EffectiveAdminAccess access, CancellationToken ct = default)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        // Bootstrap is limited to an administrator of the server. Development's
        // stub identities are exempt; that scheme is never registered in production.
        if (access.IsOpen)
        {
            return _development || PrincipalGroups.FromPrincipal(user).Contains("S-1-5-32-544");
        }

        var username = user.Identity.Name?.Trim() ?? string.Empty;

        if (username.Length > 0 && access.Users.Any(u =>
                u.Trim().Equals(username, StringComparison.OrdinalIgnoreCase)
                || user.FindAll(ClaimTypes.PrimarySid).Any(c => c.Value.Equals(u.Trim(), StringComparison.Ordinal))))
        {
            return true;
        }

        if (access.Groups.Count == 0 && access.GroupSids.Count == 0)
        {
            return false;
        }

        // Prefer SIDs copied from the Windows token (Negotiate or form login);
        // fall back to the directory for identities without group claims (dev).
        var userSids = PrincipalGroups.FromPrincipal(user);
        if (_development && userSids.Count == 0 && username.Length > 0)
        {
            // Admin authorization fails closed: a directory outage yields an empty
            // set here and therefore no admin grant, which is the safe default.
            userSids = (await _directory.GetGroupSidsAsync(username, ct)).Sids;
        }

        if (userSids.Count == 0)
        {
            return false;
        }

        if (access.GroupSids.Count > 0 && userSids.Overlaps(access.GroupSids))
        {
            return true;
        }

        foreach (var groupName in access.Groups)
        {
            var sid = await ResolveGroupSidAsync(groupName, ct);
            if (sid is not null && userSids.Contains(sid))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a group NAME to its SID, forest-wide (so a cross-domain admin
    /// group resolves). Delegates to the directory's forest-aware lookup.
    /// </summary>
    public Task<string?> ResolveGroupSidAsync(string name, CancellationToken ct = default)
        => _directory.ResolveGroupSidByNameAsync(name, ct);
}
