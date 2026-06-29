using System.IO;
using System.Security.Principal;
using Microsoft.Win32;
using OpenPrintDeploy.Client.Core;

namespace OpenPrintDeploy.Client.Cleanup;

/// <summary>One user profile's managed-printer state, as seen from SYSTEM.</summary>
/// <param name="Sid">The profile's SID (registry subkey name under ProfileList).</param>
/// <param name="UserName">Best-effort account label for logs.</param>
/// <param name="EligibleUncs">Created-origin UNCs from this profile, eligible for removal.</param>
internal sealed record ProfileManagedState(string Sid, string? UserName, IReadOnlyList<string> EligibleUncs);

/// <summary>
/// Reads every real user profile's <c>managed-printers.json</c> from the SYSTEM
/// context. The profile list (SID → profile directory) comes from
/// <c>HKLM\…\ProfileList</c>; built-in service accounts (SYSTEM, LOCAL/NETWORK
/// SERVICE) are skipped — they never run the tray.
/// </summary>
internal static class ProfileEnumerator
{
    private const string ProfileListKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public static IReadOnlyList<ProfileManagedState> EnumerateManagedProfiles()
    {
        var results = new List<ProfileManagedState>();

        using var profileList = Registry.LocalMachine.OpenSubKey(ProfileListKey);
        if (profileList is null)
        {
            CleanupLog.Warn($"Could not open HKLM\\{ProfileListKey}; no profiles enumerated.");
            return results;
        }

        foreach (var sid in profileList.GetSubKeyNames())
        {
            if (!IsRealUserSid(sid))
            {
                continue;
            }

            string? profileDir;
            using (var profileKey = profileList.OpenSubKey(sid))
            {
                profileDir = profileKey?.GetValue("ProfileImagePath") as string;
            }

            if (string.IsNullOrWhiteSpace(profileDir))
            {
                continue;
            }

            var statePath = CleanupPaths.ManagedStateUnder(profileDir);
            if (!File.Exists(statePath))
            {
                continue;
            }

            IReadOnlyList<ManagedPrinter> managed;
            try
            {
                managed = ManagedPrinterSerializer.Parse(File.ReadAllText(statePath));
            }
            catch (Exception ex)
            {
                CleanupLog.Warn($"Could not read {statePath} ({ex.Message}); skipping profile {sid}.");
                continue;
            }

            var eligible = PendingRemovalPlanner.EligibleForRemoval(managed);
            var userName = TranslateSid(sid) ?? Path.GetFileName(profileDir.TrimEnd(Path.DirectorySeparatorChar));

            var adopted = managed.Count - eligible.Count;
            CleanupLog.Info(
                $"Profile {userName} [{sid}]: {eligible.Count} printer(s) eligible for removal" +
                (adopted > 0 ? $", {adopted} adopted printer(s) kept." : "."));

            if (eligible.Count > 0)
            {
                results.Add(new ProfileManagedState(sid, userName, eligible));
            }
        }

        return results;
    }

    /// <summary>
    /// True for ordinary interactive user SIDs (<c>S-1-5-21-…</c>). Excludes the
    /// built-in service accounts S-1-5-18/19/20 and any non-account subkey.
    /// </summary>
    private static bool IsRealUserSid(string sid)
        => sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase);

    private static string? TranslateSid(string sid)
    {
        try
        {
            return new SecurityIdentifier(sid)
                .Translate(typeof(NTAccount))
                .Value;
        }
        catch
        {
            // SID no longer resolvable (deleted account, offline DC) — fine, the
            // label is cosmetic; removal keys on the SID itself.
            return null;
        }
    }
}
