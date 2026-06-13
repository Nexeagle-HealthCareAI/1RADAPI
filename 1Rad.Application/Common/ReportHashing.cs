using System.Security.Cryptography;
using System.Text;

namespace _1Rad.Application.Common;

/// <summary>
/// Deterministic content hashing for the report sign-off audit trail
/// (21 CFR Part 11 tamper evidence). The hash is computed over a canonical
/// concatenation of the report's clinical content so it is stable regardless of
/// how the fields are serialised, and the unit separator (U+001F) prevents
/// field-boundary collisions (e.g. "ab"+"c" vs "a"+"bc").
/// </summary>
public static class ReportHashing
{
    private const char Sep = ''; // ASCII unit separator — never appears in report text

    /// <summary>SHA-256 (lowercase hex) of the report's signed clinical content.</summary>
    public static string HashContent(string? findings, string? impression, string? advice)
        => Sha256Hex($"{findings ?? string.Empty}{Sep}{impression ?? string.Empty}{Sep}{advice ?? string.Empty}");

    /// <summary>SHA-256 (lowercase hex) of an arbitrary string (e.g. an addendum body).</summary>
    public static string HashText(string? text) => Sha256Hex(text ?? string.Empty);

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
