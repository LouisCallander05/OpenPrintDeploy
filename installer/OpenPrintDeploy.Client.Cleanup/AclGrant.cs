using System.Diagnostics;
using System.IO;

namespace OpenPrintDeploy.Client.Cleanup;

/// <summary>
/// Grants the Users group Modify on a single file via <c>icacls</c>. Used on the
/// removal manifest so a limited per-user logon pass can prune its own SID and,
/// once the last user is done, delete the manifest — neither of which a normal
/// user could do against a SYSTEM-owned file otherwise.
///
/// <para>
/// Scoped to the manifest file only (not the folder or the exe): the persistent
/// cleanup exe keeps the inherited Users = Read &amp; Execute, so a limited user
/// can run it but can't replace the binary other users execute at logon.
/// </para>
/// </summary>
internal static class AclGrant
{
    // S-1-5-32-545 = BUILTIN\Users, by SID so it's locale-independent.
    private const string UsersSid = "*S-1-5-32-545";

    public static void GrantUsersModify(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "icacls.exe"),
                Arguments = $"\"{filePath}\" /grant {UsersSid}:M",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var p = Process.Start(psi);
            if (p is null)
            {
                CleanupLog.Warn("Could not start icacls to grant manifest access.");
                return;
            }

            var stderr = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            if (p.ExitCode == 0)
            {
                CleanupLog.Info("Granted Users:Modify on the removal manifest.");
            }
            else
            {
                CleanupLog.Warn($"icacls returned {p.ExitCode}: {stderr.Trim()}");
            }
        }
        catch (Exception ex)
        {
            CleanupLog.Warn($"Could not grant Users access to the manifest ({ex.Message}).");
        }
    }
}
