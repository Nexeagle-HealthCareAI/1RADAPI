using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.Queries.GetReport;

public record GetReportQuery : IRequest<DiagnosticReport?>
{
    public string AppointmentId { get; init; } = string.Empty;
    // Multi-service rollout (step 6). When supplied, returns the report
    // scoped to this specific AppointmentService line. Null = legacy
    // behaviour: return the earliest-created report on the visit,
    // ignoring service Id — keeps single-service / v1-client flows
    // unchanged.
    public Guid? AppointmentServiceId { get; init; }

    // Cloud PACS-only: fetch the report written against an ImagingStudy
    // (no visit). Mutually exclusive with AppointmentId.
    public Guid? ImagingStudyId { get; init; }
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
        // Study-based (PACS-only) report — one per study.
        if (request.ImagingStudyId is Guid studyId && studyId != Guid.Empty)
        {
            return await _context.DiagnosticReports
                .Include(r => r.Addenda)
                .FirstOrDefaultAsync(r => r.ImagingStudyId == studyId, cancellationToken);
        }

        Guid.TryParse(request.AppointmentId, out var guidId);

        if (request.AppointmentServiceId.HasValue)
        {
            // Service-scoped lookup. Match by both keys so different
            // services on the same visit get their own reports.
            var serviceId = request.AppointmentServiceId.Value;
            return await _context.DiagnosticReports
                .Include(r => r.Appointment)
                .Include(r => r.Addenda)
                .FirstOrDefaultAsync(r =>
                    r.AppointmentServiceId == serviceId &&
                    ((guidId != Guid.Empty && r.AppointmentId == guidId) ||
                      r.Appointment.DisplayId == request.AppointmentId),
                    cancellationToken);
        }

        // v1 path — first report on the visit. If multi-service reports
        // exist, returns whichever was created first (typically the
        // "primary" service's report).
        return await _context.DiagnosticReports
            .Include(r => r.Appointment)
            .Include(r => r.Addenda)
            .Where(r =>
                (guidId != Guid.Empty && r.AppointmentId == guidId) ||
                r.Appointment.DisplayId == request.AppointmentId)
            .OrderBy(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
