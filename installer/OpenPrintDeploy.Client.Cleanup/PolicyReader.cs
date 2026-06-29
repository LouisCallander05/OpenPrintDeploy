using Microsoft.Win32;
using OpenPrintDeploy.Client.Core;

namespace OpenPrintDeploy.Client.Cleanup;

/// <summary>
/// Resolves the <c>RemoveManagedPrintersOnUninstall</c> policy. An explicit
/// command-line override wins (so the EXE installer can honour an
/// <c>uninstall --keep-printers</c>); otherwise the value the installer wrote to
/// HKLM is used; absent, removal defaults on.
/// </summary>
internal static class PolicyReader
{
    public static bool ShouldRemove(string? cliOverride)
    {
        if (!string.IsNullOrWhiteSpace(cliOverride))
        {
            var forced = ClientPolicy.ParseRemoveOnUninstall(cliOverride);
            CleanupLog.Info($"Policy from command line: RemoveManagedPrintersOnUninstall={forced}");
            return forced;
        }

        string? raw = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ClientPolicy.RegistrySubKey);
            raw = key?.GetValue(ClientPolicy.RemoveOnUninstallValueName) as string;
        }
        catch (Exception ex)
        {
            CleanupLog.Warn($"Could not read policy from registry ({ex.Message}); using default.");
        }

        var value = ClientPolicy.ParseRemoveOnUninstall(raw);
        CleanupLog.Info(
            $"Policy RemoveManagedPrintersOnUninstall={value} " +
            $"(registry value={(raw ?? "<unset>")}, default={ClientPolicy.RemoveOnUninstallDefault}).");
        return value;
    }
}
