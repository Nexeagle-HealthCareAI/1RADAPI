using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class Patient : BaseEntity, IHospitalContext
{
    public Guid PatientId { get; set; } = Guid.NewGuid();
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string PatientIdentifier { get; set; } = string.Empty; // MRN
    public Guid HospitalId { get; set; }
    
    // Navigation
    public Hospital Hospital { get; set; } = null!;
}
