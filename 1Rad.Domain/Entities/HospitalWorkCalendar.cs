using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// A centre's working week (migration 121): which weekdays are off and which
/// Saturdays are off. A centre with no row works Monday to Saturday, Sunday off.
/// </summary>
public class HospitalWorkCalendar : IHospitalContext
{
    public Guid HospitalId { get; set; }

    /// <summary>Weekly off days, comma-separated: "SUN" or "SAT,SUN".</summary>
    public string WeeklyOff { get; set; } = "SUN";

    /// <summary>Saturdays off: null (none), "2_4" (2nd and 4th), "1_3" (1st and 3rd).</summary>
    public string? SaturdaysOff { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}
