using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace OpenPrintDeploy.Client.Installer;

internal static class ClientInstaller
{
    private const string TrayExeName  = "OpenPrintDeploy.Client.Tray.exe";
    private const string RunValueName = "OpenPrintDeployTray";
    private const string RunKeyPath   = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    // Hive-relative path to the per-machine Run key. HKLM, so the tray launches
    // for every user logging into this machine — that's the deployment model.

    private static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "OpenPrintDeploy",
        "Tray");

    public static int Install(string? serverUrl)
    {
        var sourceDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var sourceExe = Path.Combine(sourceDir, TrayExeName);

        if (!File.Exists(sourceExe))
        {
            throw new InvalidOperationException(
                $"Could not find {TrayExeName} next to the installer. Run this exe from the extracted publish folder.");
        }

        if (string.Equals(sourceDir, InstallDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The installer is running from {InstallDir} — that's the install destination. " +
                "Run it from the publish/extracted folder instead.");
        }

        // On install/upgrade we need the running tray (in any user's session) to
        // release file handles on the binaries. Killing all instances and letting
        // the Run key relaunch on next logon is the simplest reliable approach;
        // any active user loses their tray icon momentarily but gets it back at
        // next sign-in.
        Log("Stopping any running tray instances...");
        KillRunningTray();
        Thread.Sleep(1000);

        Log($"Copying files to {InstallDir}...");
        EnsureDirectory(InstallDir);
        CopyDirectory(sourceDir, InstallDir);

        var installedExe = Path.Combine(InstallDir, TrayExeName);
        WriteAppSettings(installedExe, serverUrl);

        Log("Registering tray for auto-start at user logon...");
        SetMachineRunKey(installedExe);

        PrintInstallSummary(installedExe);
        Program.WaitForKeyIfInteractive();
        return 0;
    }

    public static int Uninstall(bool removeData)
    {
        Log("Stopping any running tray instances...");
        KillRunningTray();

        Log("Removing logon auto-start Run key...");
        RemoveMachineRunKey();

        if (Directory.Exists(InstallDir))
        {
            Log($"Removing {InstallDir}...");
            TryDeleteDirectory(InstallDir, "install directory");
        }

        if (removeData)
        {
            // Per-user state lives under each user's LOCALAPPDATA — we can't
            // touch the others' from here. Wipe the current user's at minimum;
            // for the rest, document that they're per-user throwaway state.
            var currentUserData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenPrintDeploy");
            if (Directory.Exists(currentUserData))
            {
                Log($"Removing current user's state: {currentUserData}");
                TryDeleteDirectory(currentUserData, "current user's state");
            }
            Console.WriteLine("  Note: each user's %LOCALAPPDATA%\\OpenPrintDeploy is per-user; " +
                              "only the installing account's was removed.");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine();
        Console.WriteLine("Uninstalled.");
        Console.ResetColor();
        Program.WaitForKeyIfInteractive();
        return 0;
    }

    /// <summary>
    /// Writes (or refreshes) the tray's <c>appsettings.json</c>. If a server URL
    /// wasn't provided on this invocation, we preserve whatever URL is already
    /// in place — so an upgrade install doesn't wipe the operator's config.
    /// </summary>
    private static void WriteAppSettings(string installedExe, string? serverUrl)
    {
        var settingsPath = Path.Combine(Path.GetDirectoryName(installedExe)!, "appsettings.json");

        JsonObject root;
        if (File.Exists(settingsPath))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject() ?? new JsonObject();
            }
            catch (JsonException)
            {
                root = new JsonObject();
            }
        }
        else
        {
            root = new JsonObject();
        }

        var server = root["Server"]?.AsObject() ?? new JsonObject();

        if (!string.IsNullOrWhiteSpace(serverUrl))
        {
            // Validate now so a typo doesn't get baked into appsettings.json.
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException(
                    $"--server value '{serverUrl}' is not an absolute URL (e.g. http://printsrv01:5080).");
            }
            server["BaseAddress"] = serverUrl;
        }
        else if (server["BaseAddress"] is null)
        {
            throw new InvalidOperationException(
                "First-time install: pass --server <url> (e.g. --server http://printsrv01.corp.local:5080).");
        }

        server["SyncIntervalMinutes"] ??= 60;
        root["Server"] = server;

        File.WriteAllText(
            settingsPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Log($"Wrote {settingsPath} (server={server["BaseAddress"]}, intervalMinutes={server["SyncIntervalMinutes"]})");
    }

    private static void SetMachineRunKey(string installedExe)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Could not open HKLM\\{RunKeyPath}");
        key.SetValue(RunValueName, $"\"{installedExe}\"", RegistryValueKind.String);
    }

    private static void RemoveMachineRunKey()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private static void KillRunningTray()
    {
        // Each user session has its own tray.exe process. Process.GetProcessesByName
        // returns all of them across sessions when called from an elevated process.
        var processName = Path.GetFileNameWithoutExtension(TrayExeName);
        foreach (var p in Process.GetProcessesByName(processName))
        {
            try
            {
                p.Kill();
                p.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Could not stop PID {p.Id}: {ex.Message}");
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    private static void PrintInstallSummary(string installedExe)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine();
        Console.WriteLine("Installed.");
        Console.ResetColor();
        Console.WriteLine($"  Install:    {InstallDir}");
        Console.WriteLine($"  Tray exe:   {installedExe}");
        Console.WriteLine($"  Auto-start: HKLM\\{RunKeyPath}\\{RunValueName}");
        Console.WriteLine();
        Console.WriteLine("The tray will launch automatically the next time any user logs in.");
        Console.WriteLine("To start it for the *current* user without logging out:");
        Console.WriteLine($"  start \"\" \"{installedExe}\"");
    }

    // ----- filesystem helpers -----

    private static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
    }

    private static void CopyDirectory(string source, string destination)
    {
        EnsureDirectory(destination);

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, dir);
            EnsureDirectory(Path.Combine(destination, rel));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, rel);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path, string label)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Note: could not delete {label} at {path}: {ex.Message}");
        }
    }

    private static void Log(string message) => Console.WriteLine(message);
}
