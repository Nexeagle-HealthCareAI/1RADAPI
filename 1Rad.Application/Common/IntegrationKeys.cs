using System.Security.Cryptography;
using System.Text;

namespace _1Rad.Application.Common;

/// <summary>
/// Integration API keys: <c>1rk_&lt;KeyId&gt;_&lt;secret&gt;</c>.
///   • KeyId  — 10 base-62 characters, stored in clear, used to find the row.
///   • secret — 32 random bytes, base64url; never stored.
/// The row keeps SHA-256 (hex) of the WHOLE key. A plain hash is enough here:
/// the secret is 256 random bits, so there is nothing to brute-force.
/// </summary>
public static class IntegrationKeys
{
    public const string Prefix = "1rk_";
    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int KeyIdLength = 10;

    public sealed record NewKey(string KeyId, string FullKey, string SecretHash);

    public static NewKey Generate()
    {
        var keyId = new string(Enumerable.Range(0, KeyIdLength)
            .Select(_ => Base62[RandomNumberGenerator.GetInt32(Base62.Length)])
            .ToArray());
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var full = $"{Prefix}{keyId}_{secret}";
        return new NewKey(keyId, full, Hash(full));
    }

    /// <summary>True when the text has the shape of a key (cheap check, no lookup).</summary>
    public static bool LooksLikeKey(string? text) =>
        text != null && text.StartsWith(Prefix, StringComparison.Ordinal) && TryGetKeyId(text) != null;

    /// <summary>The KeyId part of a key, or null when the text is not a key.</summary>
    public static string? TryGetKeyId(string? key)
    {
        if (string.IsNullOrEmpty(key) || !key.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var rest = key.Substring(Prefix.Length);
        var sep = rest.IndexOf('_');
        if (sep != KeyIdLength || rest.Length <= sep + 20) return null;
        var id = rest.Substring(0, sep);
        return id.All(c => Base62.Contains(c)) ? id : null;
    }

    public static string Hash(string fullKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(fullKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Constant-time comparison of a presented key against a stored hash.</summary>
    public static bool Matches(string presentedKey, string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;
        var a = Encoding.ASCII.GetBytes(Hash(presentedKey));
        var b = Encoding.ASCII.GetBytes(storedHash.ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
