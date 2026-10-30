using System.Globalization;
using System.Numerics;

namespace _1Rad.Application.Common;

/// <summary>
/// DICOM UIDs derived from 1Rad ids, using the standard UUID root "2.25"
/// (PS3.5 §B.2: 2.25 followed by the UUID as one unsigned decimal integer).
///
/// The modality worklist hands each scheduled service a Study Instance UID made
/// from its AppointmentService id. When the modality sends the images back, the
/// study's UID decodes straight to that service — an exact match, no guessing on
/// names or accession numbers.
/// </summary>
public static class DicomUids
{
    public const string UuidRoot = "2.25.";

    /// <summary>"2.25.&lt;decimal&gt;" for the given id (at most 44 characters, under the 64 limit).</summary>
    public static string FromGuid(Guid id)
    {
        var value = BigInteger.Parse("0" + id.ToString("N"), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return UuidRoot + value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The id a "2.25." UID was made from, or null for any other UID.</summary>
    public static Guid? TryToGuid(string? uid)
    {
        if (string.IsNullOrWhiteSpace(uid)) return null;
        uid = uid.Trim();
        if (!uid.StartsWith(UuidRoot, StringComparison.Ordinal)) return null;
        var digits = uid.Substring(UuidRoot.Length);
        if (digits.Length == 0 || digits.Length > 39 || !digits.All(char.IsAsciiDigit)) return null;
        if (digits.Length > 1 && digits[0] == '0') return null; // DICOM forbids leading zeros
        if (!BigInteger.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return null;
        if (value.Sign < 0) return null;

        var hex = value.ToString("x", CultureInfo.InvariantCulture).TrimStart('0');
        if (hex.Length > 32) return null;
        hex = hex.PadLeft(32, '0');
        return Guid.TryParseExact(hex, "N", out var g) ? g : null;
    }
}
