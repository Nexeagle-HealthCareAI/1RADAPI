using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// One opening of a referral partner's portal (review R9/R23; migration 107), so the
/// centre can see when and from where a link is being used. The IP is stored hashed.
/// </summary>
public class ReferralPortalView : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }
    public Guid ReferrerId { get; set; }
    public DateTime ViewedAt { get; set; } = DateTime.UtcNow;
    public string Action { get; set; } = "OPEN";                // OPEN / PROFILE / BOOKING / RENEW
    public string? IpHash { get; set; }
    public string? UserAgent { get; set; }
    public int LinkVersion { get; set; }
}
