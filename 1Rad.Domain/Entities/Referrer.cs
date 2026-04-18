using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class Referrer : BaseEntity, IHospitalContext
{
    public Guid ReferrerId { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Contact { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public Guid HospitalId { get; set; }
    
    // Navigation
    public Hospital Hospital { get; set; } = null!;
}
