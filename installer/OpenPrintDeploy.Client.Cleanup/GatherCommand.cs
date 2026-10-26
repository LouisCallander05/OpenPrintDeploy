using System.IO;
using OpenPrintDeploy.Client.Core;

namespace OpenPrintDeploy.Client.Cleanup;

/// <summary>
/// The SYSTEM-context uninstall step. It does NOT remove printers itself (it
/// can't reach other users' connections); it gathers profile SIDs from HKLM,
/// into the machine-wide manifest, arms a per-user logon task to do the actual
/// removal, and kicks off an immediate removal for whoever is signed in now.
/// </summary>
internal static class GatherCommand
{
    public static int Run(string? policyOverride)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (!identity.IsSystem && !new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) return 0;
        CleanupPaths.PrepareProtectedStorage();
        CleanupLog.Info("=== OPD uninstall: gather managed printers for removal ===");

        if (!PolicyReader.ShouldRemove(policyOverride))
        {
            CleanupLog.Info("RemoveManagedPrintersOnUninstall is off — leaving all printers in place.");
            return 0;
        }

        var profiles = ProfileEnumerator.EnumerateManagedProfiles();
        if (profiles.Count == 0)
        {
            CleanupLog.Info("No eligible user profiles found. Nothing to do.");
            return 0;
        }

        var manifest = new PendingRemoval(
            PendingRemoval.CurrentVersion,
            DateTime.UtcNow.ToString("o"),
            profiles);

        try
        {
            Directory.CreateDirectory(CleanupPaths.RootDir);
            File.WriteAllText(CleanupPaths.ManifestPath, manifest.Serialize());
            CleanupLog.Info(
                $"Wrote cleanup request for {manifest.Users.Count} profile(s) → {CleanupPaths.ManifestPath}");
        }
        catch (Exception ex)
        {
            CleanupLog.Error($"Could not write removal manifest: {ex.Message}. Aborting (no printers removed).");
            return 0; // Never fail the uninstall.
        }

        // The manifest remains SYSTEM/Administrators writable. Users record
        // completion in their own profile instead of modifying other users' entries.

        // Stage a persistent copy of this exe so the logon task and the immediate
        // pass have a target after the install directory is deleted.
        var persistentExe = StagePersistentExe();
        if (persistentExe is null)
        {
            CleanupLog.Error("Could not stage the cleanup exe; per-user removal cannot be scheduled.");
            return 0;
        }

        LogonTaskRegistrar.Register(persistentExe);
        ActiveSessionRunner.TryRemoveForActiveUser(persistentExe);

        CleanupLog.Info("Gather complete. Per-user removal will run at logon (and now for the active session).");
        return 0;
    }

    /// <summary>
    /// Copies the running exe into <see cref="CleanupPaths.PersistentDir"/>. With a
    /// self-contained single-file build, <see cref="Environment.ProcessPath"/> is
    /// the bundle exe — a single file to copy. The destination has an explicitly
    /// protected ACL granting users read/execute and administrators/SYSTEM write.
    /// </summary>
    private static string? StagePersistentExe()
    {
        try
        {
            var source = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                CleanupLog.Error($"Cannot resolve running exe path (ProcessPath='{source}').");
                return null;
            }

            Directory.CreateDirectory(CleanupPaths.PersistentDir);

            // If we're already running from the persistent location (e.g. a re-run),
            // don't copy a file onto itself.
            if (!string.Equals(
                    Path.GetFullPath(source),
                    Path.GetFullPath(CleanupPaths.PersistentExe),
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, CleanupPaths.PersistentExe, overwrite: true);
            }

            CleanupLog.Info($"Staged cleanup exe → {CleanupPaths.PersistentExe}");
            return CleanupPaths.PersistentExe;
        }
        catch (Exception ex)
        {
            CleanupLog.Error($"Could not stage cleanup exe: {ex.Message}");
            return null;
        }
    }
}
