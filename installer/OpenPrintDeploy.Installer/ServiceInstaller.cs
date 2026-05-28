using System.Diagnostics;

namespace OpenPrintDeploy.Installer;

internal static class ServiceInstaller
{
    private const string ServiceName  = "OpenPrintDeployServer";
    private const string DisplayName  = "OpenPrintDeploy Server";
    private const string Description  = "OpenPrintDeploy admin server + /sync API for zone-driven printer deployment.";
    private const string ServerExe    = "OpenPrintDeploy.Server.exe";
    private const int    Port         = 5080;
    private const string FirewallRule = "OpenPrintDeploy (5080/tcp)";

    private static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "OpenPrintDeploy");

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OpenPrintDeploy");

    public static int Install()
    {
        // AppContext.BaseDirectory is the folder containing this installer exe —
        // i.e. the extracted publish folder. The user runs us from there.
        var sourceDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var sourceExe = Path.Combine(sourceDir, ServerExe);

        if (!File.Exists(sourceExe))
        {
            throw new InvalidOperationException(
                $"Could not find {ServerExe} next to the installer. Run this exe from the extracted publish folder.");
        }

        if (string.Equals(sourceDir, InstallDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The installer is running from {InstallDir} — that's the install destination. " +
                "Run it from the publish/extracted folder instead.");
        }

        Log($"Stopping existing service '{ServiceName}' (if any)...");
        StopAndDeleteService();

        Log($"Copying files to {InstallDir}...");
        EnsureDirectory(InstallDir);
        CopyDirectory(sourceDir, InstallDir);

        var installedExe = Path.Combine(InstallDir, ServerExe);

        Log($"Registering service '{ServiceName}'...");
        // sc.exe's key= value syntax: each "key=" needs to be a separate arg
        // from its value, with no internal space. Using ArgumentList preserves
        // them as discrete argv tokens, which sc.exe parses correctly.
        ScRequired(
            "create", ServiceName,
            "binPath=",      installedExe,
            "start=",        "auto",
            "DisplayName=",  DisplayName,
            "obj=",          "LocalSystem");

        ScRequired("description", ServiceName, Description);

        // Recover from transient crashes: restart after 60s, twice, then stop trying.
        ScRequired("failure", ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000//0");

        Log($"Adding firewall rule '{FirewallRule}'...");
        // Delete first (allowed to fail if it doesn't exist), then add cleanly.
        Netsh("advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");
        NetshRequired("advfirewall", "firewall", "add", "rule",
            $"name={FirewallRule}",
            "dir=in", "action=allow",
            "protocol=TCP", $"localport={Port}",
            "profile=any");

        Log($"Starting service '{ServiceName}'...");
        var startResult = Sc("start", ServiceName);
        if (!startResult.Succeeded)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Could not start service automatically (exit {startResult.ExitCode}).");
            Console.WriteLine("  Check the Application event log; start it manually with:");
            Console.WriteLine($"    sc start {ServiceName}");
            Console.ResetColor();
        }
        else
        {
            // sc start is asynchronous; brief settle window before we declare success.
            Thread.Sleep(1500);
        }

        PrintInstallSummary();
        Program.WaitForKeyIfInteractive();
        return 0;
    }

    public static int Uninstall(bool removeData)
    {
        Log($"Stopping and removing service '{ServiceName}'...");
        StopAndDeleteService();

        Log($"Removing firewall rule '{FirewallRule}'...");
        Netsh("advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");

        if (removeData)
        {
            TryDeleteDirectory(InstallDir, "install directory");
            TryDeleteDirectory(DataDir, "database directory");
        }
        else
        {
            Console.WriteLine($"Left {InstallDir} and {DataDir} in place.");
            Console.WriteLine("Pass --remove-data to delete them as well.");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine();
        Console.WriteLine("Uninstalled.");
        Console.ResetColor();
        Program.WaitForKeyIfInteractive();
        return 0;
    }

    private static void StopAndDeleteService()
    {
        // 1060 = ERROR_SERVICE_DOES_NOT_EXIST. Anything else (0 = exists,
        // running or not; non-zero, non-1060 = some other failure) means we
        // need to attempt stop+delete.
        var query = Sc("query", ServiceName);
        if (query.ExitCode == 1060)
        {
            return;  // never installed; nothing to undo
        }

        // Stop returns success even if already stopped on some systems, or
        // 1062 (NOT_STARTED) if not running. Both are fine; we proceed.
        Sc("stop", ServiceName);

        // Give the SCM up to ~15s for the service to actually stop. We poll
        // sc.exe query and look for STATE=STOPPED.
        for (var i = 0; i < 15; i++)
        {
            Thread.Sleep(1000);
            var status = Sc("query", ServiceName);
            if (status.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)
                || status.ExitCode == 1060)
            {
                break;
            }
        }

        ScRequired("delete", ServiceName);

        // sc delete is asynchronous in the SCM — give it a beat to release the
        // service name and the file handles to the exe before we overwrite.
        Thread.Sleep(2000);
    }

    private static void PrintInstallSummary()
    {
        var host = Environment.MachineName;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine();
        Console.WriteLine("Installed.");
        Console.ResetColor();
        Console.WriteLine($"  Service:    {ServiceName} ({DisplayName})");
        Console.WriteLine($"  Identity:   LocalSystem (computer account in AD)");
        Console.WriteLine($"  Install:    {InstallDir}");
        Console.WriteLine($"  Database:   {Path.Combine(DataDir, "app.db")}");
        Console.WriteLine($"  Admin URL:  http://{host}:{Port}/");
        Console.WriteLine($"  Diag URL:   http://{host}:{Port}/admin/directory");
        Console.WriteLine();
        Console.WriteLine($"Logs: Event Viewer > Windows Logs > Application (Source: {ServiceName}).");
    }

    // ----- process helpers -----

    private readonly record struct ProcResult(int ExitCode, string Output)
    {
        public bool Succeeded => ExitCode == 0;
    }

    private static ProcResult Sc(params string[] args) => Run("sc.exe", args);

    private static void ScRequired(params string[] args)
    {
        var r = Run("sc.exe", args);
        if (!r.Succeeded)
        {
            throw new InvalidOperationException(
                $"sc.exe {string.Join(' ', args)} exited with {r.ExitCode}.\n{r.Output}");
        }
    }

    /// <summary>netsh delete may fail when the rule doesn't exist — that's fine.</summary>
    private static void Netsh(params string[] args) => Run("netsh.exe", args);

    private static void NetshRequired(params string[] args)
    {
        var r = Run("netsh.exe", args);
        if (!r.Succeeded)
        {
            throw new InvalidOperationException(
                $"netsh.exe {string.Join(' ', args)} exited with {r.ExitCode}.\n{r.Output}");
        }
    }

    private static ProcResult Run(string fileName, string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return new ProcResult(p.ExitCode, (stdout + stderr).Trim());
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
            Log($"Removing {label}: {path}");
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Note: could not delete {path}: {ex.Message}");
        }
    }

    private static void Log(string message) => Console.WriteLine(message);
}
