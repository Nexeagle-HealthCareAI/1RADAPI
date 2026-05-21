using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Commands.ReviewLeave;

public class ReviewLeaveCommandHandler
    : IRequestHandler<ReviewLeaveCommand, (bool Success, string? Error)>
{
    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
        { "approved", "rejected" };

    private readonly IApplicationDbContext _context;

    public ReviewLeaveCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<(bool Success, string? Error)> Handle(
        ReviewLeaveCommand request, CancellationToken cancellationToken)
    {
        if (!ValidStatuses.Contains(request.Status))
            return (false, "Invalid status. Allowed: approved, rejected.");

        var leave = await _context.StaffLeaveRequests
            .FirstOrDefaultAsync(l => l.LeaveRequestId == request.LeaveRequestId
                                   && l.HospitalId == request.HospitalId, cancellationToken);
        if (leave == null)
            return (false, "Leave request not found.");

        leave.Status           = request.Status.ToLowerInvariant();
        leave.ReviewedByUserId = request.ReviewedByUserId;
        leave.ReviewedAt       = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return (true, null);
    }
}
