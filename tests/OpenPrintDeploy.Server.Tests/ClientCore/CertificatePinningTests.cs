using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OpenPrintDeploy.Client.Core;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class CertificatePinningTests
{
    [Theory]
    [InlineData("AA BB CC", "AABBCC")]
    [InlineData("aa:bb:cc", "aabbcc")]
    [InlineData("  AaBbCc  ", "AaBbCc")]
    [InlineData(null, "")]
    public void Normalize_StripsSeparators_PreservingCase(string? input, string expected)
        => Assert.Equal(expected, CertificatePinning.Normalize(input));

    [Fact]
    public void Matches_TrueForSameCertificate_IgnoringFormatting()
    {
        using var cert = SelfSigned();

        // The same thumbprint, pasted with spaces and lower-cased, still matches.
        var formatted = string.Join(" ", Chunk(cert.Thumbprint!.ToLowerInvariant()));
        Assert.True(CertificatePinning.Matches(cert, formatted));
    }

    [Fact]
    public void Matches_FalseForDifferentCertificate()
    {
        using var cert = SelfSigned();
        using var other = SelfSigned();
        Assert.False(CertificatePinning.Matches(cert, other.Thumbprint));
    }

    [Fact]
    public void Matches_FalseWhenNoThumbprintConfigured()
    {
        using var cert = SelfSigned();
        Assert.False(CertificatePinning.Matches(cert, null));
        Assert.False(CertificatePinning.Matches(cert, "   "));
    }

    [Fact]
    public void Matches_FalseForNullCertificate()
        => Assert.False(CertificatePinning.Matches(null, "AABBCC"));

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=opd-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static IEnumerable<string> Chunk(string s)
    {
        for (var i = 0; i < s.Length; i += 2)
        {
            yield return s.Substring(i, Math.Min(2, s.Length - i));
        }
    }
}
