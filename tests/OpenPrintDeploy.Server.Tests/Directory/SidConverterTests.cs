using OpenPrintDeploy.Server.Directory;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Directory;

public sealed class SidConverterTests
{
    [Fact]
    public void Everyone_S_1_1_0()
    {
        // S-1-1-0: revision 1, 1 sub-authority, authority 1, sub-authority 0.
        byte[] sid = [0x01, 0x01, 0, 0, 0, 0, 0, 0x01, 0, 0, 0, 0];
        Assert.Equal("S-1-1-0", SidConverter.ToSidString(sid));
    }

    [Fact]
    public void BuiltinAdministrators_S_1_5_32_544()
    {
        // S-1-5-32-544: authority 5, sub-authorities 32 and 544.
        byte[] sid =
        [
            0x01, 0x02, 0, 0, 0, 0, 0, 0x05,
            0x20, 0, 0, 0,        // 32, little-endian
            0x20, 0x02, 0, 0,     // 544, little-endian
        ];
        Assert.Equal("S-1-5-32-544", SidConverter.ToSidString(sid));
    }

    [Fact]
    public void DomainSid_WithLargeRid_HandlesLittleEndianSubAuthorities()
    {
        // A domain SID with a 512 (Domain Admins) RID. The first three
        // sub-authorities exercise large little-endian values.
        byte[] sid =
        [
            0x01, 0x05, 0, 0, 0, 0, 0, 0x05,
            0x15, 0, 0, 0,                 // 21
            0x9C, 0xDB, 0xDA, 0x3B,        // 0x3BDADB9C = 1004198812
            0x83, 0x4B, 0x2D, 0x46,        // 0x462D4B83 = 1177373571
            0x82, 0x3F, 0xA8, 0x28,        // 0x28A83F82 = 682114946
            0x00, 0x02, 0, 0,             // 512
        ];
        Assert.Equal("S-1-5-21-1004198812-1177373571-682114946-512", SidConverter.ToSidString(sid));
    }

    [Fact]
    public void TooShort_Throws()
    {
        Assert.Throws<ArgumentException>(() => SidConverter.ToSidString([0x01, 0x00, 0x00]));
    }

    [Fact]
    public void LengthMismatchWithSubAuthorityCount_Throws()
    {
        // Declares 2 sub-authorities but only supplies bytes for one.
        byte[] sid = [0x01, 0x02, 0, 0, 0, 0, 0, 0x05, 0x20, 0, 0, 0];
        Assert.Throws<ArgumentException>(() => SidConverter.ToSidString(sid));
    }

    [Theory]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-5-21-1004198812-1177373571-682114946-512")]
    public void FromSidString_RoundTripsWithToSidString(string sid)
    {
        Assert.Equal(sid, SidConverter.ToSidString(SidConverter.FromSidString(sid)));
    }

    [Fact]
    public void FromSidString_ProducesCanonicalBinaryLayout()
    {
        byte[] expected =
        [
            0x01, 0x02, 0, 0, 0, 0, 0, 0x05,
            0x20, 0, 0, 0,        // 32, little-endian
            0x20, 0x02, 0, 0,     // 544, little-endian
        ];
        Assert.Equal(expected, SidConverter.FromSidString("S-1-5-32-544"));
    }

    [Theory]
    [InlineData("not-a-sid")]
    [InlineData("S-1")]
    [InlineData("S-x-5-21")]
    [InlineData("S-1-5-notanumber")]
    public void FromSidString_RejectsMalformed(string value)
    {
        Assert.Throws<FormatException>(() => SidConverter.FromSidString(value));
    }

    [Fact]
    public void FromSidString_RejectsBlank()
    {
        Assert.Throws<ArgumentException>(() => SidConverter.FromSidString(""));
    }
}
