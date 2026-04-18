namespace _1Rad.Application.Features.Appointments.Queries.GetAppointments;

public record AppointmentDto(
    Guid AppointmentId,
    string DisplayId,
    Guid PatientId,
    string PatientName,
    string Mobile,
    string Service,
    string Modality,
    DateTime DateTime,
    string Type,
    string Doctor,
    string Status,
    string ReferredBy,
    string ReferredContact,
    string Notes
);
