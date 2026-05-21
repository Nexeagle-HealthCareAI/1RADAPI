using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Queries.GetAttendance;

public class GetAttendanceQueryHandler : IRequestHandler<GetAttendanceQuery, List<AttendanceDto>>
{
    private readonly IApplicationDbContext _context;

    public GetAttendanceQueryHandler(IApplicationDbContext context) => _context = context;

    public async Task<List<AttendanceDto>> Handle(
        GetAttendanceQuery request, CancellationToken cancellationToken)
    {
        // Parse month bounds
        if (!DateOnly.TryParse($"{request.Month}-01", out var startDate))
            return new List<AttendanceDto>();

        var endDate = startDate.AddMonths(1).AddDays(-1);

        var rows = await _context.StaffAttendances
            .Where(a => a.HospitalId == request.HospitalId
                     && a.AttendanceDate >= startDate
                     && a.AttendanceDate <= endDate)
            .OrderBy(a => a.AttendanceDate)
            .Select(a => new AttendanceDto(
                a.AttendanceId,
                a.StaffId,
                a.AttendanceDate.ToString("yyyy-MM-dd"),
                a.Status,
                a.Note))
            .ToListAsync(cancellationToken);

        return rows;
    }
}
