namespace OpenPrintDeploy.Client.Core;

/// <summary>Printer connections use the same exact-host policy as privileged driver preparation.</summary>
public static class PrinterUncPolicy
{
    public static bool IsAllowed(string? unc, IReadOnlyCollection<string> allowedHosts, out string reason)
    {
        var allowed = DriverSourcePolicy.IsAllowed(unc, allowedHosts);
        reason = allowed ? string.Empty : "malformed UNC or host is not an allowed print server";
        return allowed;
    }
}
