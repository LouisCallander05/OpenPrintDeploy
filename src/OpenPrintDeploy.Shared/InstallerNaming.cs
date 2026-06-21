using System.Text.RegularExpressions;

namespace OpenPrintDeploy.Shared;

/// <summary>
/// Encodes/decodes the server host and optional pinned certificate thumbprint in
/// the client installer's own filename, so a one-click download + double-click
/// installs a correctly targeted, certificate-pinned client with no msiexec
/// properties.
///
/// Form: <c>OpenPrintDeploy - HOST - THUMB.msi</c> (the thumbprint segment is
/// omitted when there's nothing to pin, which is identical to the long-standing
/// <c>OpenPrintDeploy - HOST.msi</c> name). The separator is " - " — only spaces
/// and dashes — because bracket/glob characters (<c>[ ]</c>) break tooling such
/// as IntuneWinAppUtil, which treats them as wildcards. A browser duplicate-
/// download "(1)" suffix is stripped before parsing, so it never corrupts the
/// values. The earlier v0.9.5 bracket form is still parsed for back-compat.
///
/// For Intune / scripted fleet installs the filename is NOT the mechanism — pass
/// SERVER= and CERTTHUMBPRINT= as msiexec properties on the install command and
/// use any plain filename. This naming is purely for the manual double-click.
///
/// Single source of truth shared by the server (composes the download filename)
/// and the tray (parses it from the installed MSI's recorded path).
/// </summary>
public static class InstallerNaming
{
    private const string Product = "OpenPrintDeploy";
    private const string Separator = " - ";

    /// <summary>Builds the download filename carrying the host and (optionally) the pinned thumbprint.</summary>
    public static string Compose(string host, string? thumbprint)
    {
        var name = $"{Product}{Separator}{host.Trim()}";
        if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            name += $"{Separator}{thumbprint.Trim()}";
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

        var name = StripDuplicateSuffix(StripExtension(FileName(fileNameOrPath)));

        // Back-compat: the v0.9.5 "[server=H] [cert=T]" bracket form.
        var bracketHost = Bracket(name, "server");
        if (bracketHost is not null)
        {
            return new InstallerIdentity(Clean(bracketHost), Clean(Bracket(name, "cert")));
        }

        // Current form: "OpenPrintDeploy - HOST[ - THUMB]". Hostnames and hex
        // thumbprints never contain " - ", so positional split is unambiguous.
        var first = name.IndexOf(Separator, StringComparison.Ordinal);
        if (first < 0)
        {
            return default;
        }

        var rest = name[(first + Separator.Length)..];
        var second = rest.IndexOf(Separator, StringComparison.Ordinal);
        return second < 0
            ? new InstallerIdentity(Clean(rest), null)
            : new InstallerIdentity(Clean(rest[..second]), Clean(rest[(second + Separator.Length)..]));
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

    // Drops a browser duplicate-download suffix like " (1)" at the very end.
    private static string StripDuplicateSuffix(string name)
        => Regex.Replace(name, @"\s*\(\d+\)\s*$", string.Empty);

    private static string? Bracket(string name, string key)
    {
        var match = Regex.Match(name, $@"\[{key}=([^\]]+)\]", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>Server host and pinned certificate thumbprint decoded from an installer filename (either may be null).</summary>
public readonly record struct InstallerIdentity(string? Host, string? Thumbprint);
