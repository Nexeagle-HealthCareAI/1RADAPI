using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class Hospital : BaseEntity
{
    public Guid HospitalId { get; set; } = Guid.NewGuid();
    public Guid? GroupId { get; set; }
    public string? HospitalName { get; set; } = string.Empty;
    public string? HospitalAddress { get; set; } = string.Empty;
    public string? GSTIN { get; set; }
    public string? RegistrationNumber { get; set; }
    public string? PAN { get; set; }
    public string? NABHNumber { get; set; }
    // Physical location, set from the centre's own settings page (GPS
    // auto-detect or a draggable map pin). Both null until the admin sets
    // one — no location is ever inferred/guessed server-side.
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string Status { get; set; } = "Active";
    public bool IsAutoBillingEnabled { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public HospitalGroup? Group { get; set; }
    public ICollection<UserHospitalMapping> UserMappings { get; set; } = new List<UserHospitalMapping>();
    public ICollection<HospitalSubscription> Subscriptions { get; set; } = new List<HospitalSubscription>();
}
