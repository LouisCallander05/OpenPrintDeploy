using System.IO;

namespace OpenPrintDeploy.Client.Cleanup;

/// <summary>
/// Well-known machine-wide locations the uninstall flow uses. All under
/// <c>C:\ProgramData\OpenPrintDeploy</c> so the manifest survives the install
/// directory being deleted and is reachable from any user's logon task.
/// </summary>
internal static class CleanupPaths
{
    /// <summary>C:\ProgramData\OpenPrintDeploy</summary>
    public static string RootDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OpenPrintDeploy");

    /// <summary>C:\ProgramData\OpenPrintDeploy\pending-removal.json</summary>
    public static string ManifestPath { get; } = Path.Combine(RootDir, "pending-removal.json");

    /// <summary>
    /// Where the gather step copies this exe so the per-user logon task has a
    /// stable target after the install directory is gone. A running exe can't
    /// delete itself, so this folder is left behind to be cleaned by the task's
    /// own expiry; harmless residue for a pilot.
    /// </summary>
    public static string PersistentDir { get; } = Path.Combine(RootDir, "uninstall");

    /// <summary>C:\ProgramData\OpenPrintDeploy\uninstall\OpenPrintDeploy.Client.Cleanup.exe</summary>
    public static string PersistentExe { get; } = Path.Combine(
        PersistentDir, "OpenPrintDeploy.Client.Cleanup.exe");

    /// <summary>C:\ProgramData\OpenPrintDeploy\opd-uninstall.log (shared SYSTEM + per-user log).</summary>
    public static string LogPath { get; } = Path.Combine(RootDir, "opd-uninstall.log");

    /// <summary>Per-user managed state, relative to a profile root.</summary>
    public static string ManagedStateUnder(string profileDir)
        => Path.Combine(profileDir, "AppData", "Local", "OpenPrintDeploy", "managed-printers.json");
}
