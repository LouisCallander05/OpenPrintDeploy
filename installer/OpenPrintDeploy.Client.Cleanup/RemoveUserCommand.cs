using System.IO;
using System.Security.Principal;
using OpenPrintDeploy.Client.Core;

namespace OpenPrintDeploy.Client.Cleanup;

/// <summary>
/// The per-user removal step, run in a user's own context (the logon task, or the
/// immediate active-session launch). It removes exactly this user's eligible
/// printer connections from its own managed-state record and prunes that record.
/// Completion is recorded in the user's profile; the shared request stays immutable.
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
        if (manifest is null || manifest.Version != PendingRemoval.CurrentVersion)
        {
            // No manifest (already cleaned, or this user logged on after expiry).
            return 0;
        }
        var completionPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenPrintDeploy", "uninstall-completed.txt");
        var manifestId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(manifest.Serialize())));
        if (File.Exists(completionPath) && File.ReadAllText(completionPath) == manifestId) return 0;

        var entry = manifest.ForSid(sid);
        if (entry is null)
        {
            CleanupLog.Info($"No pending printer removals for the current user [{sid}].");
            return 0;
        }

        var statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenPrintDeploy", "managed-printers.json");
        var uncs = File.Exists(statePath)
            ? PendingRemovalPlanner.EligibleForRemoval(ManagedPrinterSerializer.Parse(File.ReadAllText(statePath)))
            : [];
        CleanupLog.Info($"Removing {uncs.Count} managed printer(s) for the current user [{sid}].");
        foreach (var unc in uncs)
        {
            PrinterConnections.TryRemove(unc);
        }

        PruneOwnManagedState(uncs);
        Directory.CreateDirectory(Path.GetDirectoryName(completionPath)!);
        File.WriteAllText(completionPath, manifestId);

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

}
