using MediatR;

namespace _1Rad.Application.Features.Staff.Commands.UpsertAttendance;

/// <summary>Mark or update a single day's attendance for a staff member.</summary>
public record UpsertAttendanceCommand(
    Guid StaffId,
    Guid HospitalId,
    Guid? MarkedByUserId,
    string Date,    // "YYYY-MM-DD"
    string Status,  // present | absent | halfday | late | leave
    string? Note
) : IRequest<(Guid AttendanceId, string? Error)>;
