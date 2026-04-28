using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.Queries.GetReport;

public record GetReportQuery : IRequest<DiagnosticReport?>
{
    public string AppointmentId { get; init; } = string.Empty;
}

public class GetReportQueryHandler : IRequestHandler<GetReportQuery, DiagnosticReport?>
{
    private readonly IApplicationDbContext _context;

    public GetReportQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<DiagnosticReport?> Handle(GetReportQuery request, CancellationToken cancellationToken)
    {
        Guid.TryParse(request.AppointmentId, out var guidId);
        
        var report = await _context.DiagnosticReports
            .Include(r => r.Appointment)
            .FirstOrDefaultAsync(r => 
                (guidId != Guid.Empty && r.AppointmentId == guidId) || 
                r.Appointment.DisplayId == request.AppointmentId, 
                cancellationToken);

        return report;
    }
}
