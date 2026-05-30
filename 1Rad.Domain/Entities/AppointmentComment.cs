using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

// Append-only comment trail for an Appointment. Replaces the
// overwrite-in-place behaviour of Appointments.DelayReason — that column is
// now a cache of the LATEST comment so worklist rows can render without a
// join, while this table preserves the full timeline for audit + context.
public class AppointmentComment : BaseEntity, IHospitalContext
{
    public Guid AppointmentCommentId { get; set; } = Guid.NewGuid();
    public Guid AppointmentId { get; set; }
    public Guid HospitalId { get; set; }
    public string Body { get; set; } = string.Empty;
    public Guid? AuthorUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Appointment Appointment { get; set; } = null!;
}
