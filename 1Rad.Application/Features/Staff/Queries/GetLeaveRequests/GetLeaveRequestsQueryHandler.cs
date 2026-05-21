using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Queries.GetLeaveRequests;

public class GetLeaveRequestsQueryHandler : IRequestHandler<GetLeaveRequestsQuery, List<LeaveRequestDto>>
{
    private readonly IApplicationDbContext _context;

    public GetLeaveRequestsQueryHandler(IApplicationDbContext context) => _context = context;

    public async Task<List<LeaveRequestDto>> Handle(
        GetLeaveRequestsQuery request, CancellationToken cancellationToken)
    {
        var query = _context.StaffLeaveRequests
            .Where(l => l.HospitalId == request.HospitalId);

        if (request.StaffId.HasValue)
            query = query.Where(l => l.StaffId == request.StaffId.Value);

        var rows = await query
            .OrderByDescending(l => l.AppliedOn)
            .Select(l => new LeaveRequestDto(
                l.LeaveRequestId,
                l.StaffId,
                l.LeaveType,
                l.FromDate.ToString("yyyy-MM-dd"),
                l.ToDate.ToString("yyyy-MM-dd"),
                l.Days,
                l.Reason,
                l.Status,
                l.AppliedOn.ToString("o")))
            .ToListAsync(cancellationToken);

        return rows;
    }
}
