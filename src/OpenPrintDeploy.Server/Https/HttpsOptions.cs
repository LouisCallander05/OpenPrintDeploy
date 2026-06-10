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
    /// Optional operator-supplied certificate (e.g. issued by your domain CA).
    /// When set it's used as-is; otherwise a self-signed cert is generated for
    /// this host and persisted in the machine certificate store.
    /// </summary>
    public string? PfxPath { get; set; }

    /// <summary>Password for <see cref="PfxPath"/>, if it has one.</summary>
    public string? PfxPassword { get; set; }
}
