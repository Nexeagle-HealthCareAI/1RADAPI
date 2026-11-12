using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// End-of-day cash count for a centre (review B12, migration 104). Expected figures
/// are what the day book says moved through each mode that IST day; Counted is what
/// the cashier found in the drawer. One row per centre per day; closing again
/// replaces the count and keeps who did it.
/// </summary>
public class DayClose : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }
    public DateTime BusinessDate { get; set; }          // IST calendar date (time part 00:00)
    public decimal ExpectedCash { get; set; }
    public decimal CountedCash { get; set; }
    public decimal Variance { get; set; }               // Counted − Expected
    public decimal ExpectedUpi { get; set; }
    public decimal ExpectedCard { get; set; }
    public decimal ExpectedOther { get; set; }
    public string? Note { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public string? ClosedByName { get; set; }
    public DateTime ClosedAt { get; set; } = DateTime.UtcNow;
    public int CloseCount { get; set; } = 1;

    // Migration 117 (Money ▸ Cash book). Expected in the drawer = OpeningFloat + the
    // day's net cash; Variance = CountedCash − that.
    public decimal OpeningFloat { get; set; }
    public decimal ExpectedBank { get; set; }
    public string? NoteCounts { get; set; }          // JSON {"500":30,"200":12,"coins":40}
    public string? DifferenceReason { get; set; }
    public decimal FloatKept { get; set; }           // left in the drawer for tomorrow
    public decimal HandedOver { get; set; }          // counted − float kept
    public string? HandedTo { get; set; }
}
