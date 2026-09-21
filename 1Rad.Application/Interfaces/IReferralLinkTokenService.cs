using System;

namespace _1Rad.Application.Interfaces;

// Signed capability tokens for the public doctor-referral portal (/r/{id}).
// Mirrors ITrackingTokenService but is bound to a REFERRER and domain-separated
// so a /track token can never be replayed here (and vice-versa).
//
// The token is stateless, but it carries a per-partner VERSION. The server keeps
// the partner's current version (ReferrerLinkVersion) and a token is only valid
// while its version still matches — so a partner's links can be revoked
// individually and instantly by bumping it, without rotating Jwt:Secret (which
// would log everyone out and kill every partner's links). Tokens issued before
// versions existed carry version 0.
public interface IReferralLinkTokenService
{
    /// <summary>How long a newly issued link works (ReferralLinks:TtlDays, default 90).</summary>
    TimeSpan Ttl { get; }

    string Issue(Guid referrerId, int version = 0, TimeSpan? ttl = null);

    /// <summary>Valid signature, right partner, not expired, and issued under the partner's CURRENT version.</summary>
    bool Validate(string token, Guid expectedReferrerId, int currentVersion);

    /// <summary>
    /// Reads a token's claims after verifying ONLY its signature - expiry and version are
    /// NOT checked. Lets the server recognise an expired link (to offer renewal) or read
    /// the expiry of a link it just minted. Returns false for anything not signed by us.
    /// </summary>
    bool TryReadClaims(string token, out ReferralLinkClaims claims);
}

public readonly record struct ReferralLinkClaims(Guid ReferrerId, DateTime ExpiresAtUtc, int Version)
{
    public bool IsExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;
}
