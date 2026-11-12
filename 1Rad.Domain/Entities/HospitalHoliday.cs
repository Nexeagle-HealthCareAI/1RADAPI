using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>A holiday on the centre's calendar (migration 121). One per date.</summary>
public class HospitalHoliday : IHospitalContext
{
    public Guid HolidayId { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }
    public DateOnly HolidayDate { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }
}
