using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OpenPrintDeploy.Server.Https;

/// <summary>
/// Provides the certificate Kestrel binds for HTTPS. Order of preference:
/// an operator-supplied PFX; else a self-signed cert reused from (or added to)
/// the Windows machine store; else — on a non-Windows dev box — an ephemeral
/// self-signed cert. Any failure returns null so the caller can fall back to
/// HTTP-only rather than refuse to start.
/// </summary>
public static class HttpsProvisioner
{
    private const string FriendlyName = "OpenPrintDeploy Server (self-signed)";

    public static X509Certificate2? TryEnsureCertificate(HttpsOptions opts, string host, ILogger logger)
    {
        try
        {
            // Clamp to 1..100 years. The floor stops a zero/negative config value
            // minting an already-expired cert; the ceiling keeps notAfter well
            // short of year 9999, past which DateTimeOffset.AddYears overflows and
            // would drop the server to HTTP-only.
            var validityYears = Math.Clamp(opts.SelfSignedValidityYears, 1, 100);

            X509Certificate2 cert;
            if (!string.IsNullOrWhiteSpace(opts.PfxPath))
            {
                logger.LogInformation("HTTPS: loading certificate from {Path}.", opts.PfxPath);
                cert = new X509Certificate2(
                    opts.PfxPath!, opts.PfxPassword,
                    X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
            }
            else if (OperatingSystem.IsWindows())
            {
                cert = EnsureInMachineStore(host, validityYears, logger);
            }
            else
            {
                logger.LogWarning("HTTPS: non-Windows host — using an ephemeral self-signed certificate (not persisted).");
                cert = CreateSelfSigned(host, validityYears);
            }

            if (!cert.HasPrivateKey)
            {
                logger.LogError("HTTPS: the certificate has no usable private key; starting HTTP-only.");
                return null;
            }

            return cert;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "HTTPS: could not provision a certificate; starting HTTP-only.");
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static X509Certificate2 EnsureInMachineStore(string host, int validityYears, ILogger logger)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);

        // Reuse a previously generated cert that's still valid, so the thumbprint
        // (which admins distribute to clients) stays stable across restarts.
        foreach (var existing in store.Certificates)
        {
            if (existing.FriendlyName == FriendlyName
                && existing.HasPrivateKey
                && existing.NotAfter > DateTime.Now.AddDays(7))
            {
                logger.LogInformation(
                    "HTTPS: reusing self-signed certificate {Thumbprint} (expires {Expiry:d}).",
                    existing.Thumbprint, existing.NotAfter);
                return existing;
            }
        }

        using var generated = CreateSelfSigned(host, validityYears);
        // Re-import via PFX so the private key is persisted in the machine key
        // store (the key from CreateSelfSigned is otherwise ephemeral).
        var persistable = new X509Certificate2(
            generated.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable)
        {
            FriendlyName = FriendlyName,
        };
        store.Add(persistable);

        logger.LogWarning(
            "HTTPS: generated a new self-signed certificate {Thumbprint} for '{Host}'. Clients do NOT trust it " +
            "yet — export the public certificate (certlm.msc -> Personal -> Certificates) and push it to clients' " +
            "Trusted Root Certification Authorities (GPO/Intune) before pointing them at HTTPS.",
            persistable.Thumbprint, host);

        return persistable;
    }

    private static X509Certificate2 CreateSelfSigned(string host, int validityYears)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false)); // serverAuth

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        if (!string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            san.AddDnsName(Environment.MachineName);
        }

        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());

        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(validityYears));

        // Re-import so the returned cert owns its key independently of `rsa`.
        return new X509Certificate2(
            ephemeral.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.Exportable);
    }
}
