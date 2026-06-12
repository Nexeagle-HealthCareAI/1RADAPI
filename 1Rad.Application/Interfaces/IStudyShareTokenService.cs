using System;

namespace _1Rad.Application.Interfaces
{
    public enum ShareTokenStatus { Valid, Expired, Invalid }

    /// <summary>
    /// HMAC-signed, time-limited tokens for sharing a single ImagingStudy with an
    /// external doctor via a secret link. Stateless (no DB) — the token carries
    /// the study id + expiry and is verified by signature, so an expired or
    /// tampered link is rejected without a lookup.
    /// </summary>
    public interface IStudyShareTokenService
    {
        /// <summary>Mint a share token for a study. Default TTL is 24 hours.</summary>
        string Issue(Guid imagingStudyId, TimeSpan? ttl = null);

        /// <summary>
        /// Validate a token. Returns Valid + the study id, Expired (still returns
        /// the id so the UI can show what was shared), or Invalid (bad signature
        /// / malformed → study id is Empty).
        /// </summary>
        (ShareTokenStatus Status, Guid StudyId, DateTimeOffset ExpiresAt) Validate(string token);
    }
}
