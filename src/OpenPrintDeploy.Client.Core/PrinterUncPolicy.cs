namespace OpenPrintDeploy.Client.Core;

/// <summary>
/// Decides whether a printer UNC from the server is safe to hand to the Windows
/// spooler. The server is authenticated and TLS-pinned, but this is defence in
/// depth: a compromised server must not be able to point a client's spooler at
/// an arbitrary host — that's NTLM-hash harvesting and malicious point-and-print.
/// Every connection must be a well-formed <c>\\host\share</c>, and — when an
/// allow-list of print servers is configured — its host must be on that list.
/// </summary>
public static class PrinterUncPolicy
{
    private static readonly char[] InvalidHostChars = ['/', ':', '*', '?', '"', '<', '>', '|', ' '];

    /// <summary>
    /// True when <paramref name="unc"/> is a well-formed printer UNC and its host
    /// is permitted. An empty <paramref name="allowedHosts"/> enforces only the
    /// format check (host allow-listing disabled).
    /// </summary>
    public static bool IsAllowed(string? unc, IReadOnlyCollection<string> allowedHosts, out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(unc) || !unc.StartsWith(@"\\", StringComparison.Ordinal))
        {
            reason = "not a UNC path";
            return false;
        }

        var body = unc[2..];
        var slash = body.IndexOf('\\');
        if (slash <= 0 || slash >= body.Length - 1)
        {
            reason = "missing host or share";
            return false;
        }

        var host = body[..slash];
        var share = body[(slash + 1)..];

        // A printer connection is a single \\host\share — no nested path, no
        // traversal, no shell/wildcard characters in the host.
        if (host.IndexOfAny(InvalidHostChars) >= 0
            || share.Contains('\\')
            || share.Contains("..", StringComparison.Ordinal))
        {
            reason = "malformed host or share";
            return false;
        }

        if (allowedHosts.Count > 0 && !allowedHosts.Any(h => HostsMatch(host, h)))
        {
            reason = $"'{host}' is not an allowed print server";
            return false;
        }

        return true;
    }

    // Host names are case-insensitive; treat a short name and the matching FQDN
    // as equal (printsrv01 == printsrv01.corp.local), but never short-match IPs.
    private static bool HostsMatch(string uncHost, string allowed)
    {
        if (uncHost.Equals(allowed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var a = ShortName(uncHost);
        var b = ShortName(allowed);
        return !IsIpLabel(a)
            && a.Equals(b, StringComparison.OrdinalIgnoreCase)
            && (uncHost.Contains('.') || allowed.Contains('.'));
    }

    private static string ShortName(string host)
    {
        var dot = host.IndexOf('.');
        return dot < 0 ? host : host[..dot];
    }

    private static bool IsIpLabel(string label) => label.Length > 0 && label.All(char.IsDigit);
}
