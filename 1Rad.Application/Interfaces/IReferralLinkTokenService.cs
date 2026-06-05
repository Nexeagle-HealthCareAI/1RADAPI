using System;

namespace _1Rad.Application.Interfaces;

// Signed capability tokens for the public doctor-referral portal (/r/{id}).
// Mirrors ITrackingTokenService but is bound to a REFERRER and domain-separated
// so a /track token can never be replayed here (and vice-versa). Stateless —
// no DB row, revoke en-masse by rotating Jwt:Secret.
public interface IReferralLinkTokenService
{
    string Issue(Guid referrerId, TimeSpan? ttl = null);
    bool Validate(string token, Guid expectedReferrerId);
}
