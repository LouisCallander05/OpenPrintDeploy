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

    /// <summary>
    /// Parses a canonical <c>S-1-5-21-…</c> SID string back to its binary form —
    /// the inverse of <see cref="ToSidString"/>. Needed to build the
    /// <c>(objectSid=…)</c> LDAP filter that resolves a SID to a group name.
    /// </summary>
    /// <exception cref="FormatException">The string isn't a well-formed SID.</exception>
    public static byte[] FromSidString(string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);

        var parts = sid.Split('-');
        // "S", revision, authority, then 0..15 sub-authorities.
        if (parts.Length < 3
            || !parts[0].Equals("S", StringComparison.OrdinalIgnoreCase)
            || !byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision))
        {
            throw new FormatException($"'{sid}' is not a valid SID.");
        }

        var authorityText = parts[2];
        var authority = authorityText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(authorityText[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ulong.Parse(authorityText, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (authority > 0xFFFF_FFFF_FFFFUL)
        {
            throw new FormatException($"'{sid}' has an identifier authority that exceeds 48 bits.");
        }

        var subAuthorityCount = parts.Length - 3;
        if (subAuthorityCount > byte.MaxValue)
        {
            throw new FormatException($"'{sid}' has too many sub-authorities.");
        }

        var bytes = new byte[8 + (4 * subAuthorityCount)];
        bytes[0] = revision;
        bytes[1] = (byte)subAuthorityCount;

        // 48-bit identifier authority, big-endian across bytes 2..7.
        for (var i = 0; i < 6; i++)
        {
            bytes[7 - i] = (byte)(authority & 0xFF);
            authority >>= 8;
        }

        for (var i = 0; i < subAuthorityCount; i++)
        {
            if (!uint.TryParse(parts[3 + i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sub))
            {
                throw new FormatException($"'{sid}' has a non-numeric sub-authority.");
            }

            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + (4 * i), 4), sub);
        }

        return bytes;
    }
}
