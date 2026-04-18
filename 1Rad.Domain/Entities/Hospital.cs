using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class Hospital : BaseEntity
{
    public Guid HospitalId { get; set; } = Guid.NewGuid();
    public Guid? GroupId { get; set; }
    public string HospitalName { get; set; } = string.Empty;
    public string HospitalAddress { get; set; } = string.Empty;
    public string? GSTIN { get; set; }
    public string Status { get; set; } = "Active";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public HospitalGroup? Group { get; set; }
    public ICollection<UserHospitalMapping> UserMappings { get; set; } = new List<UserHospitalMapping>();
}
