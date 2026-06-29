namespace OpenPrintDeploy.Client.Cleanup;

/// <summary>
/// Uninstall printer-cleanup tool for the Open Print Deploy client. Two modes:
///
///   OpenPrintDeploy.Client.Cleanup.exe --gather [--policy on|off]
///       Run as SYSTEM/elevated by the uninstaller. Enumerates every user
///       profile's managed state, writes a per-SID removal manifest, arms a
///       per-user logon task, and triggers an immediate removal for the active
///       session. --policy overrides the registry flag (the EXE installer passes
///       it for `uninstall --keep-printers`).
///
///   OpenPrintDeploy.Client.Cleanup.exe --remove-user
///       Run as a limited user (the logon task / active-session launch). Removes
///       this user's manifest-listed printers, then drops the user from the
///       manifest.
///
/// Always returns 0: a cleanup failure must never fail the uninstall itself.
/// Everything is logged to C:\ProgramData\OpenPrintDeploy\opd-uninstall.log.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (HasFlag(args, "--remove-user"))
            {
                return RemoveUserCommand.Run();
            }

            if (HasFlag(args, "--gather"))
            {
                return GatherCommand.Run(GetOption(args, "--policy"));
            }

            PrintUsage();
            return 0;
        }
        catch (Exception ex)
        {
            // Last-resort guard: never let an unexpected error fail the uninstall.
            CleanupLog.Error($"Unhandled error: {ex}");
            return 0;
        }
    }

    private static bool HasFlag(string[] args, string flag)
        => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Open Print Deploy Client — uninstall printer cleanup.");
        Console.WriteLine("  --gather [--policy on|off]   (SYSTEM) build the removal manifest and arm per-user removal");
        Console.WriteLine("  --remove-user                (user)   remove this user's managed printers");
    }
}
