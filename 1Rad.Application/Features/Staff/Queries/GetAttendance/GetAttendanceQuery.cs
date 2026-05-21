using MediatR;

namespace _1Rad.Application.Features.Staff.Queries.GetAttendance;

/// <summary>Get all attendance records for a hospital in a given month.</summary>
public record GetAttendanceQuery(
    Guid HospitalId,
    string Month  // "YYYY-MM"
) : IRequest<List<AttendanceDto>>;

public record AttendanceDto(
    Guid AttendanceId,
    Guid StaffId,
    string Date,    // "YYYY-MM-DD"
    string Status,
    string? Note
);
