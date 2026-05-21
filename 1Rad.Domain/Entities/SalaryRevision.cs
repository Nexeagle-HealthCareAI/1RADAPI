using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// A staff member's salary structure as of a given effective date.
/// Multiple revisions form an appraisal history. The "active" revision
/// for any date is the latest one whose <see cref="EffectiveFrom"/> &lt;= that date.
/// </summary>
public class SalaryRevision : BaseEntity, IHospitalContext
{
    public Guid RevisionId { get; set; } = Guid.NewGuid();
    public Guid StaffId { get; set; }
    public Guid HospitalId { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    // Earnings
    public decimal BasicPay { get; set; }
    public decimal Hra { get; set; }
    public decimal Travel { get; set; }
    public decimal OtherAllowances { get; set; }

    // Deductions
    public decimal PfDeduction { get; set; }
    public decimal Tds { get; set; }
    public decimal OtherDeductions { get; set; }

    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }

    // Navigation
    public StaffMember StaffMember { get; set; } = null!;
}
