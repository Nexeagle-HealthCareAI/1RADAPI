using MediatR;

namespace _1Rad.Application.Features.Staff.Commands.ReviewLeave;

/// <summary>Approve or reject a leave request.</summary>
public record ReviewLeaveCommand(
    Guid LeaveRequestId,
    Guid HospitalId,
    Guid? ReviewedByUserId,
    string Status   // approved | rejected
) : IRequest<(bool Success, string? Error)>;
