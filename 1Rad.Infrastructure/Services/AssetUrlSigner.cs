using System;
using System.Security.Cryptography;
using System.Text;
using _1Rad.Application.Interfaces;
using Microsoft.Extensions.Configuration;

namespace _1Rad.Infrastructure.Services
{
    /// <summary>
    /// HMAC-SHA256 implementation of <see cref="IAssetUrlSigner"/>. The signing
    /// key comes from <c>AssetProxy:SigningKey</c> when set, otherwise falls
    /// back to <c>Jwt:Secret</c> — which is always configured — so this works in
    /// every environment with no new ops step. The signed material is the blob
    /// PATH plus the expiry, never the host, so a storage-host URL and its
    /// CDN-fronted equivalent verify identically.
    /// </summary>
    public class AssetUrlSigner : IAssetUrlSigner
    {
        private readonly byte[] _key;

        public AssetUrlSigner(IConfiguration configuration)
        {
            var key = configuration["AssetProxy:SigningKey"];
            if (string.IsNullOrWhiteSpace(key))
                key = configuration["Jwt:Secret"];
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException(
                    "ASSET_SIGNING_KEY_MISSING: configure 'AssetProxy:SigningKey' or 'Jwt:Secret'.");
            _key = Encoding.UTF8.GetBytes(key);
        }

        public (long Exp, string Sig) Sign(string blobUrl, TimeSpan ttl)
        {
            var exp = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();
            return (exp, Compute(PathOf(blobUrl), exp));
        }

        public bool Validate(string blobUrl, long exp, string sig)
        {
            if (string.IsNullOrEmpty(sig)) return false;
            if (DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow) return false;

            var expected = Compute(PathOf(blobUrl), exp);
            // Constant-time compare to avoid leaking the signature byte-by-byte.
            var a = Encoding.ASCII.GetBytes(expected);
            var b = Encoding.ASCII.GetBytes(sig);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }

        // Canonical, host-independent identity of the blob: "/{container}/{blob}".
        private static string PathOf(string blobUrl)
        {
            try { return new Uri(blobUrl).AbsolutePath; }
            catch { return blobUrl ?? string.Empty; }
        }

        private string Compute(string path, long exp)
        {
            using var hmac = new HMACSHA256(_key);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{path}|{exp}"));
            return Base64Url(hash);
        }

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
