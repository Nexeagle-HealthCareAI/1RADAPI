using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// A patient a referring doctor asked to book, submitted from their portal link (/r/{id}).
///
/// This is deliberately NOT an Appointment. Booking one requires a Lead Specialist - the
/// centre's own supervising physician - which an outside referring doctor has no way to pick
/// (CreateAppointmentCommand rejects a booking without one). A request sits here until a staff
/// member reviews it and either books it for real (through the normal New Appointment flow,
/// which then links back via <see cref="ResultingAppointmentId"/>) or declines it.
/// </summary>
public class ReferralBookingRequest : BaseEntity, IHospitalContext
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HospitalId { get; set; }
    public Guid ReferrerId { get; set; }

    public string PatientName { get; set; } = string.Empty;
    public string? Mobile { get; set; }
    public string? Age { get; set; }
    public string? Gender { get; set; }
    // Free text the doctor typed (or picked from the centre's public service menu) - informational
    // for the front desk; the real Service/Modality/Amount are set when the appointment is created.
    public string? Modality { get; set; }
    public string? ServiceName { get; set; }
    public DateTime? PreferredDate { get; set; }
    public string? Notes { get; set; }

    // PENDING | SCHEDULED | DECLINED
    public string Status { get; set; } = "PENDING";
    public string? DeclineReason { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public DateTime? DecidedAt { get; set; }
    // Set when a staff member books it - the request and the real appointment are then linked both ways.
    public Guid? ResultingAppointmentId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
