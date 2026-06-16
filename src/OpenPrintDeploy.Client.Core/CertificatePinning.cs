using System.Security.Cryptography.X509Certificates;

namespace OpenPrintDeploy.Client.Core;

/// <summary>
/// Trust a specific server certificate by thumbprint. This lets clients accept
/// the server's self-signed certificate without distributing it to every
/// machine's trust store, and without the insecure "accept any certificate"
/// shortcut — only the exact pinned leaf is accepted. When no thumbprint is
/// configured, callers fall back to the platform's normal chain validation
/// (which is what you want for an operator/CA-issued certificate).
/// </summary>
public static class CertificatePinning
{
    /// <summary>
    /// Strips spaces, colons and any other separators a thumbprint might be
    /// pasted with, leaving the bare hex so two thumbprints compare reliably.
    /// </summary>
    public static string Normalize(string? thumbprint)
        => thumbprint is null
            ? string.Empty
            : new string(thumbprint.Where(Uri.IsHexDigit).ToArray());

    /// <summary>True when <paramref name="certificate"/>'s thumbprint matches <paramref name="expectedThumbprint"/>.</summary>
    public static bool Matches(X509Certificate2? certificate, string? expectedThumbprint)
        => certificate is not null
            && Normalize(expectedThumbprint).Length > 0
            && string.Equals(
                Normalize(certificate.Thumbprint),
                Normalize(expectedThumbprint),
                StringComparison.OrdinalIgnoreCase);
}
