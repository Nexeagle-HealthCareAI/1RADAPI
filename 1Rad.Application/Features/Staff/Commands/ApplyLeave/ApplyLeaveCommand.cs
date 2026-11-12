using MediatR;

namespace _1Rad.Application.Features.Staff.Commands.ApplyLeave;

/// <summary>Submit a new leave application for a staff member.</summary>
public record ApplyLeaveCommand(
    Guid StaffId,
    Guid HospitalId,
    string LeaveType,
    string FromDate,  // "YYYY-MM-DD"
    string ToDate,    // "YYYY-MM-DD"
    int Days,         // ignored: the server counts working days (Nov 2026)
    string? Reason
) : IRequest<(Guid LeaveRequestId, string? Error)>;
