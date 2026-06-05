using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using _1Rad.Application.Interfaces;
using Microsoft.Extensions.Configuration;

namespace _1Rad.Infrastructure.Services;

// HMAC-SHA256 signed capability token for the public doctor-referral portal.
//   payload   = referrerId (16 bytes) || expUnixSeconds (8 bytes, BE)
//   signature = HMAC-SHA256(key=Jwt:Secret, DOMAIN || payload)
//   wire      = base64url(payload) + "." + base64url(signature)
// The DOMAIN tag domain-separates these from /track tokens — a tracking token's
// signature will never validate as a referral token.
public class ReferralLinkTokenService : IReferralLinkTokenService
{
    private const int PayloadSize = 24; // 16 (Guid) + 8 (exp)
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("REFERRAL_LINK_V1");
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(365);

    private readonly byte[] _key;

    public ReferralLinkTokenService(IConfiguration configuration)
    {
        var secret = configuration["Jwt:Secret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "Jwt:Secret is not configured. ReferralLinkTokenService cannot sign tokens.");
        }
        _key = Encoding.UTF8.GetBytes(secret);
    }

    public string Issue(Guid referrerId, TimeSpan? ttl = null)
    {
        var expUnix = DateTimeOffset.UtcNow.Add(ttl ?? DefaultTtl).ToUnixTimeSeconds();
        var payload = new byte[PayloadSize];
        referrerId.TryWriteBytes(payload.AsSpan(0, 16));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(16, 8), expUnix);
        return $"{B64(payload)}.{B64(Hmac(payload))}";
    }

    public bool Validate(string token, Guid expectedReferrerId)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 2) return false;

        byte[] payload, sig;
        try { payload = UnB64(parts[0]); sig = UnB64(parts[1]); }
        catch { return false; }

        if (payload.Length != PayloadSize) return false;
        if (!CryptographicOperations.FixedTimeEquals(Hmac(payload), sig)) return false;
        if (new Guid(payload.AsSpan(0, 16)) != expectedReferrerId) return false;

        var exp = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(16, 8)));
        return DateTimeOffset.UtcNow < exp;
    }

    private byte[] Hmac(byte[] data)
    {
        using var hmac = new HMACSHA256(_key);
        var buf = new byte[Domain.Length + data.Length];
        Buffer.BlockCopy(Domain, 0, buf, 0, Domain.Length);
        Buffer.BlockCopy(data, 0, buf, Domain.Length, data.Length);
        return hmac.ComputeHash(buf);
    }

    private static string B64(byte[] d) =>
        Convert.ToBase64String(d).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] UnB64(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        switch (b.Length % 4) { case 2: b += "=="; break; case 3: b += "="; break; }
        return Convert.FromBase64String(b);
    }
}
