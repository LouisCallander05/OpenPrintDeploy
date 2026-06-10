namespace OpenPrintDeploy.Server.Https;

/// <summary>
/// What TLS ended up doing at startup, for the admin dashboard to display.
/// Registered as a singleton.
/// </summary>
/// <param name="Enabled">HTTPS was requested in config.</param>
/// <param name="Bound">A certificate was provisioned and the HTTPS port is listening.</param>
/// <param name="SelfSigned">The certificate was auto-generated (not operator-supplied).</param>
/// <param name="Host">The hostname the certificate was issued for.</param>
/// <param name="Port">The HTTPS port.</param>
/// <param name="Thumbprint">The certificate thumbprint, for distribution/verification.</param>
public sealed record HttpsStatus(
    bool Enabled,
    bool Bound,
    bool SelfSigned,
    string Host,
    int Port,
    string? Thumbprint)
{
    public static HttpsStatus Disabled { get; } =
        new(Enabled: false, Bound: false, SelfSigned: false, Host: "", Port: 0, Thumbprint: null);
}
