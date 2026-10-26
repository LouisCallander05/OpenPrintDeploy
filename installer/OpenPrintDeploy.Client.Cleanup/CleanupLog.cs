using System.Globalization;
using System.IO;
using System.Security.Principal;

namespace OpenPrintDeploy.Client.Cleanup;

/// <summary>
/// Privileged work logs to stdout for MSI capture. Standard-user removal also
/// appends to that user's own log. Never opens a shared writable log as SYSTEM.
/// Best-effort: a logging failure never aborts the uninstall.
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
                using var identity = WindowsIdentity.GetCurrent();
                // Privileged logs go to MSI stdout only. Never append as SYSTEM
                // to a path a user could have pre-created or redirected.
                if (identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenPrintDeploy");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "opd-uninstall.log"), line + Environment.NewLine);
            }
        }
        catch
        {
            // Never crash the uninstall over a logging failure.
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
