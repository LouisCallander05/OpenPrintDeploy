using Microsoft.Win32;
using OpenPrintDeploy.Client.Core;

namespace OpenPrintDeploy.Client.Cleanup;

// SYSTEM reads only machine-owned registry metadata. User-writable profile
// paths may contain junctions or network links and must never be read as SYSTEM.
internal static class ProfileEnumerator
{
    public static IReadOnlyList<PendingRemovalUser> EnumerateManagedProfiles()
    {
        using var profiles = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        return (profiles?.GetSubKeyNames() ?? [])
            .Where(sid => sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
                || sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase))
            .Select(sid => new PendingRemovalUser(sid, null, []))
            .ToList();
    }
}
