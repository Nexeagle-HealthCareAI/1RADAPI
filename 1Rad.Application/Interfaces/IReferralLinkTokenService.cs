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
    string Issue(Guid referrerId, int version = 0, TimeSpan? ttl = null);

    /// <summary>Valid signature, right partner, not expired, and issued under the partner's CURRENT version.</summary>
    bool Validate(string token, Guid expectedReferrerId, int currentVersion);
}
