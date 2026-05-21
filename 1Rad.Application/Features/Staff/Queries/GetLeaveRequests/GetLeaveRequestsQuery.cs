using MediatR;

namespace _1Rad.Application.Features.Staff.Queries.GetLeaveRequests;

/// <summary>Get all leave requests for a hospital, optionally filtered by staff.</summary>
public record GetLeaveRequestsQuery(
    Guid HospitalId,
    Guid? StaffId   // null = all staff
) : IRequest<List<LeaveRequestDto>>;

public record LeaveRequestDto(
    Guid LeaveRequestId,
    Guid StaffId,
    string LeaveType,
    string FromDate,    // "YYYY-MM-DD"
    string ToDate,      // "YYYY-MM-DD"
    int Days,
    string? Reason,
    string Status,
    string AppliedOn    // ISO 8601
);
