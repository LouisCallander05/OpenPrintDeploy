namespace OpenPrintDeploy.Client.Installer;

/// <summary>
/// Native-Windows installer for the OpenPrintDeploy tray client. Designed to
/// be wrapped as a .intunewin and deployed via Intune as a Win32 app, but the
/// exe runs standalone too: double-click triggers UAC, install args drive a
/// silent install for Intune.
///
///   OpenPrintDeploy.Client.Installer.exe install --server http://printsrv01:5080
///   OpenPrintDeploy.Client.Installer.exe uninstall
///   OpenPrintDeploy.Client.Installer.exe uninstall --remove-data
///   OpenPrintDeploy.Client.Installer.exe --help
///
/// The server can also be supplied by simply renaming the installer. A file
/// named "OpenPrintDeploy - &lt;host&gt;.exe" configures the tray to talk to
/// that host with no arguments at all — double-clicking is enough. This is the
/// zero-touch path for non-technical deployers: rename, ship, run.
/// </summary>
internal static class Program
{
    // Renaming the installer to "OpenPrintDeploy - <host>.exe" picks <host> as
    // the server. Filenames can't carry a scheme or ':' port, so we apply a
    // fixed scheme/port to the bare host. An explicit --server still wins.
    private const string FileNameServerDelimiter = " - ";
    private const string FileNameServerScheme    = "http";
    private const int    FileNameServerPort       = 5080;

    public static int Main(string[] args)
    {
        try
        {
            return Parse(args) switch
            {
                { Mode: Mode.Help } => PrintHelp(),
                { Mode: Mode.Uninstall, RemoveData: var removeData } => ClientInstaller.Uninstall(removeData),
                { Mode: Mode.Install, ServerUrl: var url } => ClientInstaller.Install(url),
                _ => PrintHelp(),
            };
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine($"ERROR: {ex.Message}");
            Console.ResetColor();
            WaitForKeyIfInteractive();
            return 1;
        }
    }

    private enum Mode { Install, Uninstall, Help }

    private readonly record struct Args(Mode Mode, string? ServerUrl, bool RemoveData);

    private static Args Parse(string[] argv)
    {
        var mode = Mode.Install;
        string? server = null;
        var removeData = false;

        for (var i = 0; i < argv.Length; i++)
        {
            var a = argv[i].ToLowerInvariant();
            switch (a)
            {
                case "--help" or "-h" or "/?":
                    mode = Mode.Help;
                    break;
                case "install":
                case "--install":
                    mode = Mode.Install;
                    break;
                case "uninstall":
                case "--uninstall":
                    mode = Mode.Uninstall;
                    break;
                case "--remove-data":
                    removeData = true;
                    break;
                case "--server" or "-s":
                    if (i + 1 < argv.Length)
                    {
                        server = argv[++i];
                    }
                    break;
            }
        }

        // An explicit --server always wins. Otherwise, fall back to a server
        // encoded in the installer's own filename. (For uninstall this is
        // irrelevant — we never write config there.)
        if (mode == Mode.Install && string.IsNullOrWhiteSpace(server))
        {
            server = TryDeriveServerFromFileName();
        }

        return new Args(mode, server, removeData);
    }

    /// <summary>
    /// Reads the server host out of the installer's own filename. A file named
    /// "OpenPrintDeploy - 0912SPS01.services.education.vic.gov.au.exe" yields
    /// "http://0912SPS01.services.education.vic.gov.au:5080". Returns null when
    /// the filename carries no "<c> - </c>" delimiter (e.g. the default
    /// "OpenPrintDeploy.Client.Installer.exe"), so a normal run is unaffected.
    /// </summary>
    private static string? TryDeriveServerFromFileName()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        var idx = name.IndexOf(FileNameServerDelimiter, StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        var host = name[(idx + FileNameServerDelimiter.Length)..].Trim();

        // Defensive: if the file was named with a wrapper extension that
        // GetFileNameWithoutExtension didn't strip (e.g. "...au.msi.exe" leaves
        // "...au.msi"), drop a trailing installer extension. Real hostnames
        // never end in .msi/.exe so this can't eat a legitimate label.
        foreach (var ext in new[] { ".msi", ".exe" })
        {
            if (host.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                host = host[..^ext.Length].Trim();
            }
        }

        if (host.Length == 0)
        {
            return null;
        }

        var url = $"{FileNameServerScheme}://{host}:{FileNameServerPort}";
        Console.WriteLine($"No --server given; using server from installer filename: {url}");
        return url;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("Open Print Deploy Client — installer (per-machine, auto-start at logon).");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  OpenPrintDeploy.Client.Installer.exe install --server <url>");
        Console.WriteLine("                                                Install the tray and configure the server URL.");
        Console.WriteLine("  OpenPrintDeploy.Client.Installer.exe install");
        Console.WriteLine("                                                Install/upgrade keeping the existing server URL.");
        Console.WriteLine("  OpenPrintDeploy.Client.Installer.exe uninstall");
        Console.WriteLine("                                                Stop the tray for all users, remove the Run key and files.");
        Console.WriteLine("                                                Leaves per-user state (managed-printers.json) in place.");
        Console.WriteLine("  OpenPrintDeploy.Client.Installer.exe uninstall --remove-data");
        Console.WriteLine("                                                Also wipe per-user state on this machine.");
        Console.WriteLine("  OpenPrintDeploy.Client.Installer.exe --help   Show this help.");
        Console.WriteLine();
        Console.WriteLine("Zero-touch alternative — no arguments needed:");
        Console.WriteLine("  Rename the installer to \"OpenPrintDeploy - <host>.exe\" and run it.");
        Console.WriteLine("  e.g. \"OpenPrintDeploy - printsrv01.corp.local.exe\"");
        Console.WriteLine($"       configures the server as {FileNameServerScheme}://printsrv01.corp.local:{FileNameServerPort}");
        Console.WriteLine("  An explicit --server overrides the name.");
        Console.WriteLine();
        Console.WriteLine("Must be run elevated (double-click triggers UAC; Intune runs as SYSTEM).");
        Console.WriteLine();
        Console.WriteLine("Example Intune install command:");
        Console.WriteLine("  OpenPrintDeploy.Client.Installer.exe install --server http://printsrv01.corp.local:5080");
        WaitForKeyIfInteractive();
        return 0;
    }

    internal static void WaitForKeyIfInteractive()
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Press any key to close.");
        try { Console.ReadKey(intercept: true); } catch { /* no console attached */ }
    }
}
