namespace OpenPrintDeploy.Installer;

/// <summary>
/// Native-Windows installer for OpenPrintDeploy.Server. Replaces the
/// PowerShell install scripts because some EDR products (notably Cylance)
/// block PS execution by default — a plain .NET exe doing the same
/// service-registration work isn't subject to PS script-block policies.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        var mode = NormalizedMode(args);
        var removeData = args.Any(a => a.Equals("--remove-data", StringComparison.OrdinalIgnoreCase));

        try
        {
            return mode switch
            {
                Mode.Help      => PrintHelp(),
                Mode.Uninstall => ServiceInstaller.Uninstall(removeData),
                Mode.Install   => ServiceInstaller.Install(),
                _              => PrintHelp(),
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

    private static Mode NormalizedMode(string[] args)
    {
        foreach (var raw in args)
        {
            switch (raw.ToLowerInvariant())
            {
                case "--help" or "-h" or "/?":
                    return Mode.Help;
                case "--uninstall" or "uninstall":
                    return Mode.Uninstall;
                case "--install" or "install":
                    return Mode.Install;
            }
        }
        return Mode.Install;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("OpenPrintDeploy.Installer  —  installs the server as a Windows service.");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  OpenPrintDeploy.Installer.exe                  Install (default).");
        Console.WriteLine("  OpenPrintDeploy.Installer.exe --uninstall      Stop and remove the service. Keeps the DB.");
        Console.WriteLine("  OpenPrintDeploy.Installer.exe --uninstall --remove-data");
        Console.WriteLine("                                                 Also delete install dir + database.");
        Console.WriteLine("  OpenPrintDeploy.Installer.exe --help           Show this help.");
        Console.WriteLine();
        Console.WriteLine("Must be run elevated (the exe self-elevates via UAC on double-click).");
        WaitForKeyIfInteractive();
        return 0;
    }

    internal static void WaitForKeyIfInteractive()
    {
        // When launched from Explorer (double-click) we want the window to
        // stay open so the user can read the output. When piped/redirected
        // from a script we don't.
        if (Console.IsInputRedirected)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Press any key to close.");
        try { Console.ReadKey(intercept: true); } catch { /* no console attached */ }
    }
}
