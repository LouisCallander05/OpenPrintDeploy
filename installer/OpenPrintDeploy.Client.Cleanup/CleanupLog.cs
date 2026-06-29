using System.Globalization;
using System.IO;
using System.Security.Principal;

namespace OpenPrintDeploy.Client.Cleanup;

/// <summary>
/// Append-only log shared by the SYSTEM gather pass and every per-user removal
/// pass, so the whole uninstall story is in one file for field diagnosis. Each
/// line is stamped with the running identity (SYSTEM vs the user) because the
/// passes interleave across logons. Best-effort: a logging failure never aborts
/// the uninstall. Also echoes to stdout so MSI/installer logs capture it too.
/// </summary>
internal static class CleanupLog
{
    private static readonly object Lock = new();
    private static readonly string Who = ResolveWho();

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);
    public static void Error(string message) => Write("ERR", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} " +
                   $"[{level}] [{Who}] {message}";
        Console.WriteLine(line);
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(CleanupPaths.RootDir);
                File.AppendAllText(CleanupPaths.LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Never crash the uninstall over a logging failure (e.g. a limited
            // user without write access if the ACL grant didn't take).
        }
    }

    private static string ResolveWho()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var label = id.Name;
            if (id.IsSystem)
            {
                label += " (SYSTEM)";
            }

            return label;
        }
        catch
        {
            return "unknown";
        }
    }
}
