using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OpenPrintDeploy.Server.Auth;

/// <summary>
/// Validates domain credentials with the Windows <c>LogonUser</c> API and returns
/// the resulting <see cref="WindowsIdentity"/>. Unlike an LDAP password bind, the
/// returned token carries the user's full group set across all trusted domains
/// (the Kerberos PAC), exactly as a normal Windows sign-in would — so a
/// cross-domain admin group resolves with no cross-domain LDAP. Windows-only; the
/// admin Basic scheme is production-only (dev uses the header scheme).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsLogon
{
    private const int LOGON32_LOGON_NETWORK = 3;
    private const int LOGON32_PROVIDER_DEFAULT = 0;

    /// <summary>
    /// Returns a live <see cref="WindowsIdentity"/> on success (the caller owns it
    /// and must dispose it), or null when the credentials are rejected.
    /// </summary>
    public static WindowsIdentity? Validate(string username, string password)
    {
        SplitUserDomain(username, out var user, out var domain);

        if (!LogonUser(user, domain, password, LOGON32_LOGON_NETWORK, LOGON32_PROVIDER_DEFAULT, out var token))
        {
            return null;
        }

        using (token)
        {
            // WindowsIdentity duplicates the token, so disposing `token` here is
            // safe — the returned identity keeps its own copy.
            return new WindowsIdentity(token.DangerousGetHandle());
        }
    }

    private static void SplitUserDomain(string raw, out string user, out string? domain)
    {
        var slash = raw.IndexOf('\\');
        if (slash >= 0)
        {
            domain = raw[..slash];
            user = raw[(slash + 1)..];
            return;
        }

        // A UPN (user@domain) or a bare name: pass through with no separate
        // domain argument — LogonUser resolves the UPN, or uses the local domain.
        domain = null;
        user = raw;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool LogonUser(
        string lpszUsername,
        string? lpszDomain,
        string lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out SafeAccessTokenHandle phToken);
}
