using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

// Per-partner state for the public doctor portal links.
//
// VERSION (revocation): portal links are signed, stateless tokens; each carries the
// version it was issued under. Revoking a partner's links bumps Version, which
// instantly invalidates every token issued under an older one. A partner with NO row
// is at version 0 - which is also what every link issued before this table existed
// carries, so nothing breaks until someone actually revokes.
//
// LAST-SENT / AUTO-RENEW (lifetime): links now expire (90 days by default). Every
// deliberate send (email / WhatsApp from the Doctor Links tab, or a renewal) records
// the channel, the expiry of the link it carried, and the portal origin it used. A
// daily job renews AutoRenew links shortly before they expire, over the same channel
// and to the same portal - and a doctor holding an expired link can ask for a fresh one
// (delivered only to their registered contact). Revoking turns AutoRenew off.
public class ReferrerLinkVersion : BaseEntity, IHospitalContext
{
    public Guid ReferrerId { get; set; }
    public Guid HospitalId { get; set; }
    public int Version { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }

    public DateTime? LastSentAt { get; set; }
    public string? LastSentChannel { get; set; }      // "whatsapp" | "email"
    public DateTime? LastSentExpiresAt { get; set; }  // expiry of the link that send carried
    public string? LastSentBaseUrl { get; set; }      // portal origin used - reused by renewals, never taken from a request
    public bool AutoRenew { get; set; }
}
