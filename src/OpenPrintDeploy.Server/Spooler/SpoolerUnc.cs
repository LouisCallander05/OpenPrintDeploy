namespace OpenPrintDeploy.Server.Spooler;

/// <summary>
/// Pure helper for assembling the <c>\\server\share</c> UNC the import writes
/// to the DB. Isolated from WMI so it can be unit-tested without a spooler.
/// </summary>
public static class SpoolerUnc
{
    /// <summary>
    /// Builds <c>\\<paramref name="server"/>\<paramref name="shareName"/></c>,
    /// tolerating leading/trailing slashes and surrounding whitespace in either
    /// input. Falls back to <see cref="Environment.MachineName"/> when
    /// <paramref name="server"/> is null or blank.
    /// </summary>
    /// <exception cref="ArgumentException">The share name is null, blank, or only slashes.</exception>
    public static string Build(string? server, string shareName)
    {
        ArgumentNullException.ThrowIfNull(shareName);

        var srv = (server ?? string.Empty).Trim().Trim('\\');
        if (srv.Length == 0)
        {
            srv = Environment.MachineName;
        }

        var share = shareName.Trim().Trim('\\');
        if (share.Length == 0)
        {
            throw new ArgumentException("Share name is required.", nameof(shareName));
        }

        return $@"\\{srv}\{share}";
    }
}
