using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace OpenPrintDeploy.Server.Https;

/// <summary>
/// Admin-triggered maintenance of the auto-generated self-signed certificate.
/// Removing it from the machine store forces <see cref="HttpsProvisioner"/> to
/// mint a fresh one (with the current <c>SelfSignedValidityYears</c>) on the next
/// service start — the supported way to "renew" or lengthen a self-signed cert,
/// since a certificate's validity can't be changed in place. The new cert has a
/// NEW thumbprint, so pinned clients must be re-pinned afterwards.
/// </summary>
public static class CertificateMaintenance
{
    /// <summary>
    /// Removes the OpenPrintDeploy self-signed certificate(s) from
    /// LocalMachine\My. Returns the number removed. The running server keeps
    /// serving the in-memory cert until the service is restarted, at which point
    /// a fresh certificate is generated.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static int RemoveSelfSigned(ILogger logger)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);

        var removed = 0;
        foreach (var cert in store.Certificates)
        {
            if (cert.FriendlyName == HttpsProvisioner.FriendlyName)
            {
                store.Remove(cert);
                removed++;
                logger.LogWarning(
                    "Removed self-signed certificate {Thumbprint}; a new one is generated on next service start.",
                    cert.Thumbprint);
            }

            cert.Dispose();
        }

        return removed;
    }
}
