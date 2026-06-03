using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>
/// Decides whether this machine can authenticate to the server with the
/// signed-in user's Windows identity (Integrated Auth). That's true when the
/// device is joined to the on-prem AD domain or to Entra (Azure AD) — in either
/// case the logged-in user has an identity the server can validate. A standalone
/// (workgroup) laptop has neither, so the tray must collect explicit domain
/// credentials instead.
/// </summary>
internal static class DomainJoin
{
    /// <summary>
    /// True if integrated auth is plausibly available (domain- or Entra-joined).
    /// Detection uses <c>dsregcmd /status</c>, the OS tool that reports both join
    /// types. If detection can't run, we assume <c>true</c> (the historical
    /// behaviour) so a glitch never forces a credential prompt on a domain fleet —
    /// a genuine auth rejection (401) still triggers the explicit-credentials
    /// fallback.
    /// </summary>
    public static bool IsIntegratedAuthAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("dsregcmd", "/status")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return true;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            return IsYes(output, "DomainJoined")
                || IsYes(output, "AzureAdJoined")
                || IsYes(output, "EnterpriseJoined");
        }
        catch
        {
            // dsregcmd missing or blocked — assume integrated auth and let a 401
            // drive the fallback rather than prompting unnecessarily.
            return true;
        }
    }

    private static bool IsYes(string output, string key)
        => JoinLine(key).IsMatch(output);

    // Matches a "    <Key> : YES" line from dsregcmd's status block.
    private static Regex JoinLine(string key)
        => new($@"^\s*{Regex.Escape(key)}\s*:\s*YES\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
}
