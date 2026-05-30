using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using _1Rad.Application.Interfaces;
using Microsoft.Extensions.Configuration;

namespace _1Rad.Infrastructure.Services;

// HMAC-SHA256 signed tokens for the /track QR.
//
// Format (base64url-encoded for URL safety):
//   payload | signature
//
//   payload   = appointmentId (16 bytes) || expUnixSeconds (8 bytes, BE)
//   signature = HMAC-SHA256(payload, key=Jwt:Secret)
//
// Wire form: base64url(payload) + "." + base64url(signature)
//
// Size: payload 32 chars, signature 43 chars, separator 1 → ~76 chars. Fits
// comfortably in a QR code alongside the existing path.
public class TrackingTokenService : ITrackingTokenService
{
    private const int PayloadSize = 24; // 16 (Guid) + 8 (exp)
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(365);

    private readonly byte[] _key;

    public TrackingTokenService(IConfiguration configuration)
    {
        var secret = configuration["Jwt:Secret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            // Refuse to start with no signing key — silent generation against
            // an empty key would produce predictable signatures.
            throw new InvalidOperationException(
                "Jwt:Secret is not configured. TrackingTokenService cannot sign tokens.");
        }
        _key = Encoding.UTF8.GetBytes(secret);
    }

    public string Issue(Guid appointmentId, TimeSpan? ttl = null)
    {
        var lifetime = ttl ?? DefaultTtl;
        var expUnix = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();

        var payload = new byte[PayloadSize];
        appointmentId.TryWriteBytes(payload.AsSpan(0, 16));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(16, 8), expUnix);

        var signature = Hmac(payload);
        return $"{Base64UrlEncode(payload)}.{Base64UrlEncode(signature)}";
    }

    public bool Validate(string token, Guid expectedAppointmentId)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 2) return false;

        byte[] payload;
        byte[] presentedSig;
        try
        {
            payload = Base64UrlDecode(parts[0]);
            presentedSig = Base64UrlDecode(parts[1]);
        }
        catch
        {
            return false;
        }

        if (payload.Length != PayloadSize) return false;

        // Recompute expected signature and compare in constant time. Without
        // CryptographicOperations.FixedTimeEquals, a side-channel timing
        // attack could probe the signature one byte at a time.
        var expectedSig = Hmac(payload);
        if (!CryptographicOperations.FixedTimeEquals(expectedSig, presentedSig)) return false;

        // Bind the token to its embedded appointmentId — refuse mismatch so a
        // leaked token can't be re-pointed at a different appointment.
        var tokenAppointmentId = new Guid(payload.AsSpan(0, 16));
        if (tokenAppointmentId != expectedAppointmentId) return false;

        var expUnix = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(16, 8));
        var expUtc = DateTimeOffset.FromUnixTimeSeconds(expUnix);
        if (DateTimeOffset.UtcNow >= expUtc) return false;

        return true;
    }

    private byte[] Hmac(byte[] data)
    {
        using var hmac = new HMACSHA256(_key);
        return hmac.ComputeHash(data);
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var b64 = s.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "=";  break;
        }
        return Convert.FromBase64String(b64);
    }
}
