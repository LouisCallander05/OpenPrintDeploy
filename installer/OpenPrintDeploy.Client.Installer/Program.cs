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
/// </summary>
internal static class Program
{
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

        return new Args(mode, server, removeData);
    }

    private static int PrintHelp()
    {
        Console.WriteLine("OpenPrintDeploy.Client.Installer  —  installs the tray client per-machine.");
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
