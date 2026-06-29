using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;

namespace OpenPrintDeploy.Client.Cleanup;

/// <summary>
/// Registers the per-user AtLogon task that does the actual printer removal in
/// each user's own context (SYSTEM can't reach a logged-off user's connections).
///
/// <para>
/// The task uses a <em>group</em> principal — Users (<c>S-1-5-32-545</c>) — with
/// least-privilege rights and an interactive-logon trigger, so it runs once for
/// whoever signs in, as them. It carries a 30-day end boundary and
/// <c>DeleteExpiredTaskAfter</c>, so Task Scheduler removes the task itself once
/// the migration window passes — a limited user can't delete a SYSTEM-owned task,
/// so expiry is how it self-cleans.
/// </para>
/// </summary>
internal static class LogonTaskRegistrar
{
    public const string TaskName = @"OpenPrintDeploy\Remove Managed Printers";

    /// <summary>Days the logon task stays armed before Task Scheduler auto-deletes it.</summary>
    private const int ArmedDays = 30;

    public static bool Register(string exePath)
    {
        var xml = BuildTaskXml(exePath, DateTime.Now);
        var xmlPath = Path.Combine(Path.GetTempPath(), "opd-remove-managed-printers.xml");

        try
        {
            // schtasks /XML wants UTF-16; write it that way to be safe with any
            // unusual characters in the exe path.
            File.WriteAllText(xmlPath, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            var ok = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            if (ok)
            {
                CleanupLog.Info($"Registered logon task \"{TaskName}\" (armed {ArmedDays} days).");
            }
            else
            {
                CleanupLog.Error($"Failed to register logon task \"{TaskName}\".");
            }

            return ok;
        }
        catch (Exception ex)
        {
            CleanupLog.Error($"Exception registering logon task: {ex.Message}");
            return false;
        }
        finally
        {
            try { if (File.Exists(xmlPath)) File.Delete(xmlPath); } catch { /* best effort */ }
        }
    }

    private static string BuildTaskXml(string exePath, DateTime now)
    {
        // Task Scheduler timestamps are local ISO-8601 without offset.
        var start = now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var end = now.AddDays(ArmedDays).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var exe = SecurityElement.Escape(exePath);

        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Removes OpenPrintDeploy-managed printers for this user after the client was uninstalled.</Description>
            <URI>\{TaskName}</URI>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <StartBoundary>{start}</StartBoundary>
              <EndBoundary>{end}</EndBoundary>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <GroupId>S-1-5-32-545</GroupId>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <StartWhenAvailable>true</StartWhenAvailable>
            <AllowHardTerminate>true</AllowHardTerminate>
            <DeleteExpiredTaskAfter>PT0S</DeleteExpiredTaskAfter>
            <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
            <Enabled>true</Enabled>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{exe}</Command>
              <Arguments>--remove-user</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;
    }

    private static bool RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var p = Process.Start(psi);
        if (p is null)
        {
            return false;
        }

        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (!string.IsNullOrWhiteSpace(stdout)) CleanupLog.Info($"  schtasks: {stdout.Trim()}");
        if (!string.IsNullOrWhiteSpace(stderr)) CleanupLog.Warn($"  schtasks: {stderr.Trim()}");

        return p.ExitCode == 0;
    }
}
