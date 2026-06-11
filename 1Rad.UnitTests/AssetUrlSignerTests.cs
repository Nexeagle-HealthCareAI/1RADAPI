using _1Rad.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace _1Rad.UnitTests;

/// <summary>
/// Security properties of the proxy-asset capability signer (finding 1/2 fix):
/// a signature must round-trip, expire, reject tampering, be bound to the exact
/// blob, and validate independent of the URL host. These are what make it safe
/// to serve a private PHI container to an unauthenticated <c>fetch(sliceUrl)</c>.
/// </summary>
public class AssetUrlSignerTests
{
    private const string BlobA = "https://1radstorage.blob.core.windows.net/dicom-files/h1/a1/series/000/0001.dcm";
    private const string BlobB = "https://1radstorage.blob.core.windows.net/dicom-files/h1/a1/series/000/0002.dcm";

    private static AssetUrlSigner Signer(string? dedicatedKey = null, string? jwtSecret = "unit-test-jwt-secret-please-change")
    {
        var cfg = new Mock<IConfiguration>();
        cfg.Setup(c => c["AssetProxy:SigningKey"]).Returns(dedicatedKey);
        cfg.Setup(c => c["Jwt:Secret"]).Returns(jwtSecret);
        return new AssetUrlSigner(cfg.Object);
    }

    [Fact]
    public void Sign_then_Validate_roundtrips()
    {
        var signer = Signer();
        var (exp, sig) = signer.Sign(BlobA, TimeSpan.FromHours(8));

        signer.Validate(BlobA, exp, sig).Should().BeTrue();
    }

    [Fact]
    public void Validate_rejects_expired_signature()
    {
        var signer = Signer();
        // Negative TTL → exp is already in the past.
        var (exp, sig) = signer.Sign(BlobA, TimeSpan.FromMinutes(-1));

        signer.Validate(BlobA, exp, sig).Should().BeFalse();
    }

    [Fact]
    public void Validate_rejects_tampered_signature()
    {
        var signer = Signer();
        var (exp, sig) = signer.Sign(BlobA, TimeSpan.FromHours(8));

        // Flip one character of the signature.
        var first = sig[0] == 'A' ? 'B' : 'A';
        var tampered = first + sig.Substring(1);

        signer.Validate(BlobA, exp, tampered).Should().BeFalse();
    }

    [Fact]
    public void Validate_rejects_empty_signature()
    {
        var signer = Signer();
        var (exp, _) = signer.Sign(BlobA, TimeSpan.FromHours(8));

        signer.Validate(BlobA, exp, "").Should().BeFalse();
    }

    [Fact]
    public void Validate_rejects_extended_expiry()
    {
        // An attacker can't push the expiry out: exp is part of the signed
        // material, so changing it invalidates the signature.
        var signer = Signer();
        var (exp, sig) = signer.Sign(BlobA, TimeSpan.FromHours(8));

        signer.Validate(BlobA, exp + 100_000, sig).Should().BeFalse();
    }

    [Fact]
    public void Signature_is_bound_to_the_blob_path()
    {
        // A capability minted for one slice must not read a sibling slice.
        var signer = Signer();
        var (exp, sig) = signer.Sign(BlobA, TimeSpan.FromHours(8));

        signer.Validate(BlobB, exp, sig).Should().BeFalse();
    }

    [Fact]
    public void Validate_is_host_agnostic()
    {
        // Signed material is the blob PATH, so a CDN-fronted URL with the same
        // path validates against a signature minted on the storage host.
        var signer = Signer();
        var (exp, sig) = signer.Sign(BlobA, TimeSpan.FromHours(8));

        const string cdnSamePath = "https://cdn.1rad.app/dicom-files/h1/a1/series/000/0001.dcm";
        signer.Validate(cdnSamePath, exp, sig).Should().BeTrue();
    }

    [Fact]
    public void Falls_back_to_Jwt_Secret_when_no_dedicated_key()
    {
        var signer = Signer(dedicatedKey: null, jwtSecret: "fallback-secret-value");
        var (exp, sig) = signer.Sign(BlobA, TimeSpan.FromHours(8));

        signer.Validate(BlobA, exp, sig).Should().BeTrue();
    }

    [Fact]
    public void Different_keys_do_not_cross_validate()
    {
        var signerA = Signer(dedicatedKey: "key-one");
        var signerB = Signer(dedicatedKey: "key-two");
        var (exp, sig) = signerA.Sign(BlobA, TimeSpan.FromHours(8));

        signerB.Validate(BlobA, exp, sig).Should().BeFalse();
    }

    [Fact]
    public void Throws_when_no_signing_key_is_configured()
    {
        var act = () => Signer(dedicatedKey: null, jwtSecret: null);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*ASSET_SIGNING_KEY_MISSING*");
    }
}
