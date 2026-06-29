namespace OpenPrintDeploy.Client.Core;

/// <summary>
/// Names and defaults for client policy, shared by the installers and the
/// uninstall cleanup tool so they can never disagree on a key name. These are
/// plain strings (no Windows API), so they live in the cross-platform core.
/// </summary>
public static class ClientPolicy
{
    /// <summary>HKLM key the MSI/EXE installer writes client config under.</summary>
    public const string RegistrySubKey = @"SOFTWARE\OpenPrintDeploy\Client";

    /// <summary>
    /// Registry value (under <see cref="RegistrySubKey"/>) gating uninstall
    /// printer removal. "1"/"0". Absent is treated as on, matching the default.
    /// </summary>
    public const string RemoveOnUninstallValueName = "RemoveManagedPrintersOnUninstall";

    /// <summary>MSI property an admin sets to override the flag (REMOVEMANAGEDPRINTERS=0).</summary>
    public const string RemoveOnUninstallMsiProperty = "REMOVEMANAGEDPRINTERS";

    /// <summary>Removal is on unless the admin explicitly turns it off.</summary>
    public const bool RemoveOnUninstallDefault = true;

    /// <summary>
    /// Interprets a registry/property string ("1", "0", "true", "false", null)
    /// as the boolean flag, falling back to <see cref="RemoveOnUninstallDefault"/>
    /// when unset or unrecognised.
    /// </summary>
    public static bool ParseRemoveOnUninstall(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return RemoveOnUninstallDefault;
        }

        var v = raw.Trim();
        if (v is "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (v is "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return RemoveOnUninstallDefault;
    }
}
