using System.Security.Claims;

namespace OpenPrintDeploy.Server.Directory;

/// <summary>
/// Reads the group SIDs out of an authenticated principal's token. After a
/// Kerberos/NTLM sign-in the Windows token (the PAC) already carries every group
/// the user belongs to <em>across all trusted domains</em> — assembled by the
/// domain controllers during authentication. Using these means a server joined
/// to one domain resolves users from another trusted domain correctly, with no
/// cross-domain LDAP. Returns empty for identities with no group claims (e.g. the
/// dev header auth), where callers fall back to a directory lookup.
/// </summary>
public static class PrincipalGroups
{
    public static IReadOnlySet<string> FromPrincipal(ClaimsPrincipal user)
        => user.FindAll(ClaimTypes.GroupSid)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);
}
