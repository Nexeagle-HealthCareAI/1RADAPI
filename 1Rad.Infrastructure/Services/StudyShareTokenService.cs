using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using _1Rad.Application.Interfaces;
using Microsoft.Extensions.Configuration;

namespace _1Rad.Infrastructure.Services;

// HMAC-SHA256 signed, time-limited tokens for sharing one ImagingStudy via a
// secret link. Mirrors TrackingTokenService.
//
//   payload   = studyId (16 bytes) || expUnixSeconds (8 bytes, BE)
//   signature = HMAC-SHA256(payload, key=Jwt:Secret)
//   wire form = base64url(payload) + "." + base64url(signature)
public class StudyShareTokenService : IStudyShareTokenService
{
    private const int PayloadSize = 24; // 16 (Guid) + 8 (exp)
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    private readonly byte[] _key;

    public StudyShareTokenService(IConfiguration configuration)
    {
        var secret = configuration["Jwt:Secret"];
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException(
                "Jwt:Secret is not configured. StudyShareTokenService cannot sign tokens.");
        _key = Encoding.UTF8.GetBytes(secret);
    }

    public string Issue(Guid imagingStudyId, TimeSpan? ttl = null)
    {
        var expUnix = DateTimeOffset.UtcNow.Add(ttl ?? DefaultTtl).ToUnixTimeSeconds();
        var payload = new byte[PayloadSize];
        imagingStudyId.TryWriteBytes(payload.AsSpan(0, 16));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(16, 8), expUnix);
        var signature = Hmac(payload);
        return $"{Base64UrlEncode(payload)}.{Base64UrlEncode(signature)}";
    }

    public (ShareTokenStatus Status, Guid StudyId, DateTimeOffset ExpiresAt) Validate(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return (ShareTokenStatus.Invalid, Guid.Empty, default);
        var parts = token.Split('.');
        if (parts.Length != 2) return (ShareTokenStatus.Invalid, Guid.Empty, default);

        byte[] payload, presentedSig;
        try
        {
            payload = Base64UrlDecode(parts[0]);
            presentedSig = Base64UrlDecode(parts[1]);
        }
        catch { return (ShareTokenStatus.Invalid, Guid.Empty, default); }

        if (payload.Length != PayloadSize) return (ShareTokenStatus.Invalid, Guid.Empty, default);

        // Constant-time signature check first — a tampered token is Invalid.
        var expectedSig = Hmac(payload);
        if (!CryptographicOperations.FixedTimeEquals(expectedSig, presentedSig))
            return (ShareTokenStatus.Invalid, Guid.Empty, default);

        var studyId = new Guid(payload.AsSpan(0, 16));
        var expUnix = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(16, 8));
        var expUtc = DateTimeOffset.FromUnixTimeSeconds(expUnix);

        // Signature is valid → the study id is trustworthy even when expired, so
        // the share page can still tell the viewer what was shared.
        if (DateTimeOffset.UtcNow >= expUtc) return (ShareTokenStatus.Expired, studyId, expUtc);
        return (ShareTokenStatus.Valid, studyId, expUtc);
    }

    private byte[] Hmac(byte[] data)
    {
        using var hmac = new HMACSHA256(_key);
        return hmac.ComputeHash(data);
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var b64 = s.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
        }
        return Convert.FromBase64String(b64);
    }
}
