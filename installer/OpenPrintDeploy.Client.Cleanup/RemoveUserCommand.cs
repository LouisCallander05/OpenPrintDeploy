using System.IO;
using System.Security.Principal;
using OpenPrintDeploy.Client.Core;

namespace OpenPrintDeploy.Client.Cleanup;

/// <summary>
/// The per-user removal step, run in a user's own context (the logon task, or the
/// immediate active-session launch). It removes exactly this user's eligible
/// printer connections from the manifest, prunes its own managed-state record,
/// then drops its SID from the manifest so re-runs are no-ops and the manifest
/// empties as users sign in.
/// </summary>
internal static class RemoveUserCommand
{
    public static int Run()
    {
        string sid;
        try
        {
            var current = WindowsIdentity.GetCurrent().User;
            if (current is null)
            {
                CleanupLog.Warn("Could not resolve current user SID; nothing removed.");
                return 0;
            }

            sid = current.Value;
        }
        catch (Exception ex)
        {
            CleanupLog.Warn($"Could not resolve current user SID ({ex.Message}); nothing removed.");
            return 0;
        }

        var manifest = ReadManifest();
        if (manifest is null)
        {
            // No manifest (already cleaned, or this user logged on after expiry).
            return 0;
        }

        var entry = manifest.ForSid(sid);
        if (entry is null || entry.Uncs.Count == 0)
        {
            CleanupLog.Info($"No pending printer removals for the current user [{sid}].");
            return 0;
        }

        CleanupLog.Info($"Removing {entry.Uncs.Count} managed printer(s) for the current user [{sid}].");
        foreach (var unc in entry.Uncs)
        {
            PrinterConnections.TryRemove(unc);
        }

        PruneOwnManagedState(entry.Uncs);
        WriteBackWithoutSid(manifest, sid);

        CleanupLog.Info("Per-user printer removal complete.");
        return 0;
    }

    private static PendingRemoval? ReadManifest()
    {
        try
        {
            return File.Exists(CleanupPaths.ManifestPath)
                ? PendingRemoval.Parse(File.ReadAllText(CleanupPaths.ManifestPath))
                : null;
        }
        catch (Exception ex)
        {
            CleanupLog.Warn($"Could not read manifest ({ex.Message}).");
            return null;
        }
    }

    /// <summary>
    /// Drops the just-removed UNCs from this user's own managed-printers.json so
    /// its record matches reality. Adopted printers (not in the removed set) stay.
    /// Best-effort — the app is being uninstalled, so a stale record is harmless.
    /// </summary>
    private static void PruneOwnManagedState(IReadOnlyList<string> removed)
    {
        try
        {
            var statePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenPrintDeploy", "managed-printers.json");

            if (!File.Exists(statePath))
            {
                return;
            }

            var removedSet = removed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var kept = ManagedPrinterSerializer.Parse(File.ReadAllText(statePath))
                .Where(m => !removedSet.Contains(m.Unc))
                .ToList();

            File.WriteAllText(statePath, ManagedPrinterSerializer.Serialize(kept));
        }
        catch (Exception ex)
        {
            CleanupLog.Warn($"Could not prune own managed state ({ex.Message}).");
        }
    }

    /// <summary>
    /// Removes this SID from the manifest. Deletes the manifest entirely once the
    /// last user is done (granted Modify on the file, a limited user can delete
    /// it). Last-writer-wins on a concurrent logon is acceptable: the worst case
    /// is a leftover SID whose next pass re-runs an idempotent removal.
    /// </summary>
    private static void WriteBackWithoutSid(PendingRemoval manifest, string sid)
    {
        try
        {
            var remaining = manifest.WithoutSid(sid);
            if (remaining.Users.Count == 0)
            {
                File.Delete(CleanupPaths.ManifestPath);
                CleanupLog.Info("Last user processed — removed the manifest.");
            }
            else
            {
                File.WriteAllText(CleanupPaths.ManifestPath, remaining.Serialize());
                CleanupLog.Info($"Dropped this user from the manifest; {remaining.Users.Count} user(s) remain.");
            }
        }
        catch (Exception ex)
        {
            // A failure here only means a redundant (idempotent) re-run later.
            CleanupLog.Warn($"Could not update the manifest after removal ({ex.Message}).");
        }
    }
}
