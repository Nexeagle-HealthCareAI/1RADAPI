using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

public class ServiceCharge : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Modality { get; set; } = string.Empty; // X-RAY, MRI, etc.
    public string ServiceName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    
    public decimal ReferralCutValue { get; set; } = 0;
    
    public Guid? TemplateId { get; set; }
    public ReportTemplate? Template { get; set; }
    
    public Guid HospitalId { get; set; }
    public Hospital Hospital { get; set; } = null!;
}
