using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Commands.ApplyLeave;

public class ApplyLeaveCommandHandler
    : IRequestHandler<ApplyLeaveCommand, (Guid LeaveRequestId, string? Error)>
{
    private readonly IApplicationDbContext _context;

    public ApplyLeaveCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<(Guid LeaveRequestId, string? Error)> Handle(
        ApplyLeaveCommand request, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParse(request.FromDate, out var from))
            return (Guid.Empty, "Invalid fromDate — expected YYYY-MM-DD.");
        if (!DateOnly.TryParse(request.ToDate, out var to))
            return (Guid.Empty, "Invalid toDate — expected YYYY-MM-DD.");
        if (to < from)
            return (Guid.Empty, "toDate cannot be before fromDate.");
        if (request.Days <= 0)
            return (Guid.Empty, "Days must be at least 1.");
        if (string.IsNullOrWhiteSpace(request.LeaveType))
            return (Guid.Empty, "LeaveType is required.");

        var staffExists = await _context.StaffMembers
            .AnyAsync(s => s.StaffId == request.StaffId && s.HospitalId == request.HospitalId, cancellationToken);
        if (!staffExists)
            return (Guid.Empty, "Staff not found.");

        var entry = new StaffLeaveRequest
        {
            StaffId    = request.StaffId,
            HospitalId = request.HospitalId,
            LeaveType  = request.LeaveType.Trim(),
            FromDate   = from,
            ToDate     = to,
            Days       = request.Days,
            Reason     = request.Reason?.Trim(),
        };
        _context.StaffLeaveRequests.Add(entry);
        await _context.SaveChangesAsync(cancellationToken);
        return (entry.LeaveRequestId, null);
    }
}
