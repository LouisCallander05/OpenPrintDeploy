using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace OpenPrintDeploy.Client.Installer;

internal static class ClientInstaller
{
    private const string TrayExeName  = "OpenPrintDeploy.Client.Tray.exe";
    private const string RunValueName = "OpenPrintDeployTray";
    private const string RunKeyPath   = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    // Logical name of the embedded tray folder (zipped). Present only in the
    // released single-file installer; absent in plain dev builds. Kept in sync
    // with the <LogicalName> in the .csproj.
    private const string PayloadResourceName = "tray-payload.zip";
    // Hive-relative path to the per-machine Run key. HKLM, so the tray launches
    // for every user logging into this machine — that's the deployment model.

    private static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "OpenPrintDeploy",
        "Tray");

    public static int Install(string? serverUrl)
    {
        // On install/upgrade we need the running tray (in any user's session) to
        // release file handles on the binaries. Killing all instances and letting
        // the Run key relaunch on next logon is the simplest reliable approach;
        // any active user loses their tray icon momentarily but gets it back at
        // next sign-in.
        Log("Stopping any running tray instances...");
        KillRunningTray();
        Thread.Sleep(1000);

        Log($"Installing files to {InstallDir}...");
        EnsureDirectory(InstallDir);
        StageTrayFiles();

        // Files that came out of a zip downloaded from the internet carry the
        // Mark-of-the-Web (a Zone.Identifier alternate data stream). The .NET
        // runtime's assembly loader honours that for self-contained WPF
        // dependencies — load fails with FileNotFoundException for things
        // like WindowsBase.dll. Strip the ADS off everything we just placed.
        Log("Stripping Mark-of-the-Web from installed files...");
        UnblockDirectory(InstallDir);

        var installedExe = Path.Combine(InstallDir, TrayExeName);
        if (!File.Exists(installedExe))
        {
            throw new InvalidOperationException(
                $"Install incomplete: {TrayExeName} was not found in {InstallDir} after staging files.");
        }

        WriteAppSettings(installedExe, serverUrl);

        Log("Registering tray for auto-start at user logon...");
        SetMachineRunKey(installedExe);

        Log("Starting the tray for the current session...");
        var started = UserSessionLauncher.TryLaunch(installedExe);

        PrintInstallSummary(installedExe, started);
        Program.WaitForKeyIfInteractive();
        return 0;
    }

    /// <summary>
    /// Puts the tray binaries into <see cref="InstallDir"/>. The released
    /// installer is a single self-extracting exe — it carries the whole tray
    /// folder as an embedded zip, so it installs with no companion files (the
    /// Intune-friendly, "download one file" path). A plain dev build has no
    /// embedded payload and instead copies the tray that sits next to the
    /// installer in a publish folder.
    /// </summary>
    private static void StageTrayFiles()
    {
        var payload = typeof(ClientInstaller).Assembly.GetManifestResourceStream(PayloadResourceName);
        if (payload is not null)
        {
            Log("Extracting bundled tray payload...");
            using (payload)
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                archive.ExtractToDirectory(InstallDir, overwriteFiles: true);
            }
            return;
        }

        // No embedded payload (dev/folder publish): copy the tray sitting next
        // to us instead.
        var sourceDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var sourceExe = Path.Combine(sourceDir, TrayExeName);
        if (!File.Exists(sourceExe))
        {
            throw new InvalidOperationException(
                $"This installer has no embedded payload and {TrayExeName} isn't next to it. " +
                "Use the released single-file installer, or run from the extracted publish folder.");
        }
        if (string.Equals(sourceDir, InstallDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The installer is running from {InstallDir} — that's the install destination. " +
                "Run it from the publish/extracted folder instead.");
        }

        Log($"Copying files from {sourceDir}...");
        CopyDirectory(sourceDir, InstallDir);
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
        Console.WriteLine("Open Print Deploy Client uninstalled.");
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
        else
        {
            // Existing BaseAddress is either missing OR an empty string (the
            // shipped default) — either way, the operator needs to supply one.
            // Without this guard, the installer happily preserved the empty
            // value from the published file and the tray crashed at first run.
            var existing = server["BaseAddress"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(existing))
            {
                throw new InvalidOperationException(
                    "First-time install: pass --server <url> (e.g. --server http://printsrv01.corp.local:5080), " +
                    "or rename the installer to \"OpenPrintDeploy - <host>.exe\" and run it.");
            }
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

    private static void PrintInstallSummary(string installedExe, bool started)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine();
        Console.WriteLine("Open Print Deploy Client installed.");
        Console.ResetColor();
        Console.WriteLine($"  Install:    {InstallDir}");
        Console.WriteLine($"  Tray exe:   {installedExe}");
        Console.WriteLine($"  Auto-start: HKLM\\{RunKeyPath}\\{RunValueName}");
        Console.WriteLine();

        if (started)
        {
            Console.WriteLine("Open Print Deploy Client is now running, and will relaunch automatically at every logon.");
        }
        else
        {
            // No interactive session to launch into (e.g. an Intune install
            // before anyone has signed in). The Run key handles it from here.
            Console.WriteLine("Open Print Deploy Client will launch automatically the next time any user logs in.");
            Console.WriteLine("To start it for the *current* user without logging out, pick the line for your shell:");
            Console.WriteLine($"  PowerShell:  & \"{installedExe}\"");
            Console.WriteLine($"  cmd:         start \"\" \"{installedExe}\"");
        }
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

    /// <summary>
    /// Deletes the <c>:Zone.Identifier</c> alternate data stream from every
    /// file in <paramref name="root"/>. That's the NTFS mechanism Windows uses
    /// to flag a file as "downloaded from the internet" (Mark-of-the-Web);
    /// it's what makes the .NET assembly loader refuse to load WPF runtime
    /// DLLs in a self-contained tray app when those DLLs came out of a
    /// zipped GitHub release.
    /// </summary>
    private static void UnblockDirectory(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                // The NTFS ADS path syntax — File.Delete passes it through
                // to the Win32 DeleteFile, which removes just the stream.
                File.Delete(file + ":Zone.Identifier");
            }
            catch
            {
                // No ADS on this file, file locked, or filesystem isn't NTFS.
                // Best-effort — if MOTW survives somewhere, the user can
                // still Unblock-File manually.
            }
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
