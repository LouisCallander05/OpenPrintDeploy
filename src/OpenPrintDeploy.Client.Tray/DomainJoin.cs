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
    /// types. The standalone sign-in path is enabled only when the output
    /// explicitly says NO for both DomainJoined and AzureAdJoined. If detection
    /// fails or is ambiguous, we assume integrated auth so a probe glitch cannot
    /// expose an irrelevant sign-in action on managed laptops.
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

            var domainJoined = ReadJoinState(output, "DomainJoined");
            var entraJoined = ReadJoinState(output, "AzureAdJoined");

            // Only a confirmed NO/NO is standalone. Missing, malformed, or new
            // dsregcmd output remains on the safe integrated-auth path and keeps
            // the manual sign-in menu hidden.
            return domainJoined != false || entraJoined != false;
        }
        catch
        {
            // dsregcmd missing or blocked — assume integrated auth. A 401 will
            // rebuild that session and retry without prompting.
            return true;
        }
    }

    private static bool? ReadJoinState(string output, string key)
    {
        var match = JoinLine(key).Match(output);
        return !match.Success
            ? null
            : string.Equals(match.Groups[1].Value, "YES", StringComparison.OrdinalIgnoreCase);
    }

    // Matches a "    <Key> : YES|NO" line from dsregcmd's status block.
    private static Regex JoinLine(string key)
        => new($@"^\s*{Regex.Escape(key)}\s*:\s*(YES|NO)\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
}
