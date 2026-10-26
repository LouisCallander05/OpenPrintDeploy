using System.IO;

namespace OpenPrintDeploy.Client.Cleanup;

/// <summary>
/// Well-known machine-wide locations the uninstall flow uses. All under
/// <c>C:\ProgramData\OpenPrintDeploy-Cleanup</c> so the manifest survives the install
/// directory being deleted and is reachable from any user's logon task.
/// </summary>
internal static class CleanupPaths
{
    /// <summary>Protected machine-wide cleanup directory, readable by users.</summary>
    public static string RootDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OpenPrintDeploy-Cleanup");

    /// <summary>Immutable per-uninstall request, writable only by SYSTEM/administrators.</summary>
    public static string ManifestPath { get; } = Path.Combine(RootDir, "pending-removal.json");

    /// <summary>
    /// Where the gather step copies this exe so the per-user logon task has a
    /// stable target after the install directory is gone. A running exe can't
    /// delete itself. Task expiry removes the scheduled task, not these files;
    /// administrators can remove the protected directory after the cleanup window.
    /// </summary>
    public static string PersistentDir { get; } = Path.Combine(RootDir, "uninstall");

    /// <summary>Protected executable used after the installed copy is removed.</summary>
    public static string PersistentExe { get; } = Path.Combine(
        PersistentDir, "OpenPrintDeploy.Client.Cleanup.exe");

    public static void PrepareProtectedStorage()
    {
        OpenPrintDeploy.Shared.Drivers.PrivateDirectory.Create(RootDir, allowUsersRead: true);
        OpenPrintDeploy.Shared.Drivers.PrivateDirectory.ValidateExistingFile(ManifestPath);
        OpenPrintDeploy.Shared.Drivers.PrivateDirectory.Create(PersistentDir, allowUsersRead: true);
        OpenPrintDeploy.Shared.Drivers.PrivateDirectory.ValidateExistingFile(PersistentExe);
    }
}
