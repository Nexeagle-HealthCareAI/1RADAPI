using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class Patient : BaseEntity, IHospitalContext
{
    public Guid PatientId { get; set; } = Guid.NewGuid();
    public string FullName { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string Age { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty; // Male, Female, Other
    public string Village { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string PatientIdentifier { get; set; } = string.Empty; // MRN
    public string SourceOfInfo { get; set; } = string.Empty;
    public Guid HospitalId { get; set; }
    
    // Navigation
    public Hospital Hospital { get; set; } = null!;
}
