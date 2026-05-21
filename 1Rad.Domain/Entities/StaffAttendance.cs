using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// One row per staff member per calendar day. Upserted by date.
/// Status values: present | absent | halfday | late | leave
/// </summary>
public class StaffAttendance : BaseEntity, IHospitalContext
{
    public Guid AttendanceId { get; set; } = Guid.NewGuid();
    public Guid StaffId { get; set; }
    public Guid HospitalId { get; set; }

    public DateOnly AttendanceDate { get; set; }

    /// <summary>present | absent | halfday | late | leave</summary>
    public string Status { get; set; } = "present";

    public string? Note { get; set; }

    public Guid? MarkedByUserId { get; set; }
    public DateTime MarkedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public StaffMember StaffMember { get; set; } = null!;
}
