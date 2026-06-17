using System.Text.RegularExpressions;

namespace OpenPrintDeploy.Shared;

/// <summary>
/// Encodes/decodes the server host and optional pinned certificate thumbprint in
/// the client installer's own filename, so a one-click download installs a
/// correctly targeted, certificate-pinned client with no msiexec properties.
///
/// Form: <c>OpenPrintDeploy [server=HOST] [cert=THUMB].msi</c> (the cert token is
/// omitted when there's nothing to pin). The values live inside <c>[ ]</c>
/// brackets on purpose: when a browser saves a second copy it appends
/// "<c> (1)</c>" BEFORE the extension — outside the brackets — so the tokens
/// survive the duplicate-download rename intact. A legacy
/// <c>OpenPrintDeploy - HOST.msi</c> name is still parsed (host only).
///
/// This is the single source of truth shared by the server (which composes the
/// download filename) and the tray (which parses it from the installed MSI's
/// recorded path), so the two can never drift.
/// </summary>
public static class InstallerNaming
{
    private const string Product = "OpenPrintDeploy";
    private const string LegacyDelimiter = " - ";

    /// <summary>Builds the download filename carrying the host and (optionally) the pinned thumbprint.</summary>
    public static string Compose(string host, string? thumbprint)
    {
        var name = $"{Product} [server={host.Trim()}]";
        if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            name += $" [cert={thumbprint.Trim()}]";
        }

        return name + ".msi";
    }

    /// <summary>Parses the host and pinned thumbprint out of an installer filename or full path.</summary>
    public static InstallerIdentity Parse(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return default;
        }

        var name = StripExtension(FileName(fileNameOrPath));

        var host = Bracket(name, "server");
        if (host is null && name.Contains(LegacyDelimiter, StringComparison.Ordinal))
        {
            var idx = name.IndexOf(LegacyDelimiter, StringComparison.Ordinal);
            host = name[(idx + LegacyDelimiter.Length)..];
        }

        return new InstallerIdentity(CleanHost(host), Clean(Bracket(name, "cert")));
    }

    private static string FileName(string path)
    {
        var slash = path.LastIndexOfAny(['\\', '/']);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static string StripExtension(string name)
    {
        foreach (var ext in new[] { ".msi", ".exe" })
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^ext.Length];
            }
        }

        return name;
    }

    private static string? Bracket(string name, string key)
    {
        var match = Regex.Match(name, $@"\[{key}=([^\]]+)\]", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    // The bracket-less legacy form can't shield the host from a duplicate-download
    // " (1)" suffix or a leftover extension, so scrub those here.
    private static string? CleanHost(string? host)
        => Clean(StripExtension(Regex.Replace(host ?? string.Empty, @"\s*\(\d+\)\s*$", string.Empty)));

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>Server host and pinned certificate thumbprint decoded from an installer filename (either may be null).</summary>
public readonly record struct InstallerIdentity(string? Host, string? Thumbprint);
