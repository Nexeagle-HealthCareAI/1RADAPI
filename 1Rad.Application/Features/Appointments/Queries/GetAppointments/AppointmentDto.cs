namespace _1Rad.Application.Features.Appointments.Queries.GetAppointments;

public record AppointmentDto(
    Guid AppointmentId,
    string? DisplayId,
    Guid PatientId,
    string? PatientName,
    string? Mobile,
    string? PatientAge,
    string? PatientGender,
    string? PatientIdentifier,
    string? Service,
    string? Modality,
    DateTime DateTime,
    string? Type,
    string? Doctor,
    string? Status,
    string? ReferredBy,
    string? ReferredContact,
    string? Notes,
    string? TechnicianComments,
    Guid? TechnicianId,
    DateTime? ScannedAt,
    decimal Amount = 0,
    decimal ReferralCutValue = 0
);

