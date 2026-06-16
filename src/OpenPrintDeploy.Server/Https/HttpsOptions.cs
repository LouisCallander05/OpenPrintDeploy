namespace OpenPrintDeploy.Server.Https;

/// <summary>
/// TLS configuration, bound from the <c>Https</c> section. Off by default — when
/// disabled the server keeps its original HTTP-only binding untouched.
/// </summary>
public sealed class HttpsOptions
{
    public const string SectionName = "Https";

    /// <summary>Serve over TLS. When false, nothing in this feature runs.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Plain-HTTP port. Kept on by default so existing clients keep working while
    /// you roll HTTPS out. Set to 0 to stop serving HTTP once every client is on
    /// HTTPS and trusts the certificate.
    /// </summary>
    public int HttpPort { get; set; } = 5080;

    /// <summary>HTTPS port.</summary>
    public int HttpsPort { get; set; } = 5443;

    /// <summary>
    /// Enforce TLS once clients trust the certificate: every plain-HTTP request is
    /// 307-redirected to HTTPS and HSTS is sent. Off by default so it doesn't
    /// break the rollout window where HTTP and HTTPS coexist — flip it to true
    /// (and optionally set <see cref="HttpPort"/> to 0) after the certificate is
    /// distributed and clients are pointed at HTTPS. Only takes effect when a
    /// certificate was actually bound; otherwise the server stays HTTP-only rather
    /// than redirect to a dead port.
    /// </summary>
    public bool RequireHttps { get; set; }

    /// <summary>
    /// Optional operator-supplied certificate (e.g. issued by your domain CA).
    /// When set it's used as-is; otherwise a self-signed cert is generated for
    /// this host and persisted in the machine certificate store.
    /// </summary>
    public string? PfxPath { get; set; }

    /// <summary>Password for <see cref="PfxPath"/>, if it has one.</summary>
    public string? PfxPassword { get; set; }

    /// <summary>
    /// Validity, in years, of the auto-generated self-signed certificate. Long by
    /// default so its thumbprint stays stable for the life of the deployment:
    /// clients that pin the thumbprint never break on a rotation, because in
    /// practice it never rotates. Clamped to 1..100 — 100 years is effectively
    /// permanent, and the cap keeps the expiry date well clear of the year-9999
    /// ceiling that would otherwise overflow and drop the server to HTTP-only.
    /// Ignored when <see cref="PfxPath"/> supplies an operator/CA certificate.
    /// </summary>
    public int SelfSignedValidityYears { get; set; } = 100;
}
