namespace OpenPrintDeploy.Client.Core;

/// <summary>Privileged requests require exact host matches; no short-name/FQDN wildcard trust.</summary>
public static class DriverSourcePolicy
{
    public static bool IsAllowed(string? unc, IReadOnlyCollection<string> hosts)
    {
        if (unc is null || unc.Length > 1024 || !unc.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var parts = unc[2..].Split('\\');
        return parts.Length == 2 && parts.All(p => p.Length > 0 && p is not "." and not ".."
            && !p.EndsWith('.') && !p.EndsWith(' ') && !p.Any(c => c < 32 || "/:*?\"<>|".Contains(c)))
            && !parts[0].Contains(' ') && hosts.Any(h => h.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
    }
}
