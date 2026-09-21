using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

// Per-partner "link generation" for the public doctor portal. Portal links are
// signed, stateless tokens; each one carries the version it was issued under.
// Revoking a partner's links bumps Version, which instantly invalidates every
// token issued under an older one (they no longer match). A partner with NO row
// is at version 0 — which is also what every link issued before this table
// existed carries, so nothing breaks until someone actually revokes.
public class ReferrerLinkVersion : BaseEntity, IHospitalContext
{
    public Guid ReferrerId { get; set; }
    public Guid HospitalId { get; set; }
    public int Version { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }
}
