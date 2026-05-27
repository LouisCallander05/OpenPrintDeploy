using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace OpenPrintDeploy.Server.Directory;

/// <summary>
/// Converts a binary SID (as returned by AD's <c>tokenGroups</c> attribute) to
/// its canonical <c>S-1-5-21-…</c> string form. Hand-rolled rather than using
/// <see cref="System.Security.Principal.SecurityIdentifier"/> so the Server
/// project builds and unit-tests on non-Windows dev machines — that type is
/// Windows-only and trips the CA1416 platform analyzer under
/// <c>TreatWarningsAsErrors</c>.
/// </summary>
public static class SidConverter
{
    /// <summary>
    /// Binary layout: byte 0 = revision, byte 1 = sub-authority count,
    /// bytes 2–7 = 48-bit big-endian identifier authority, then
    /// <c>count</c> × 4-byte little-endian sub-authorities.
    /// </summary>
    /// <exception cref="ArgumentException">The buffer isn't a well-formed SID.</exception>
    public static string ToSidString(byte[] sid)
    {
        ArgumentNullException.ThrowIfNull(sid);

        if (sid.Length < 8)
        {
            throw new ArgumentException("SID is too short to contain a header.", nameof(sid));
        }

        int revision = sid[0];
        int subAuthorityCount = sid[1];

        var expectedLength = 8 + (4 * subAuthorityCount);
        if (sid.Length != expectedLength)
        {
            throw new ArgumentException(
                $"SID length {sid.Length} does not match its sub-authority count " +
                $"{subAuthorityCount} (expected {expectedLength} bytes).",
                nameof(sid));
        }

        // 48-bit identifier authority, big-endian across bytes 2..7.
        ulong authority = 0;
        for (var i = 2; i < 8; i++)
        {
            authority = (authority << 8) | sid[i];
        }

        var builder = new StringBuilder("S-");
        builder.Append(revision);
        builder.Append('-');

        // Windows renders the authority in decimal when it fits in 32 bits,
        // otherwise as 0x-prefixed hex.
        if (authority <= uint.MaxValue)
        {
            builder.Append(authority);
        }
        else
        {
            builder.Append("0x").Append(authority.ToString("X12", CultureInfo.InvariantCulture));
        }

        for (var i = 0; i < subAuthorityCount; i++)
        {
            var subAuthority = BinaryPrimitives.ReadUInt32LittleEndian(sid.AsSpan(8 + (4 * i), 4));
            builder.Append('-').Append(subAuthority);
        }

        return builder.ToString();
    }
}
