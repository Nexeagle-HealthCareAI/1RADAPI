using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.Queries.GetReportsDelta;

// Bulk delta-pull for the Phase B1 offline-first cache. The single-record
// GET /reporting/report/{id} is unchanged — pages still hit it for the
// authoritative "fetch this exact report" path. The sync engine uses this
// endpoint instead so it can ask "what's changed since <ts>?" in one round
// trip and bulk-apply to local IndexedDB.
public record GetReportsDeltaQuery(
    DateTime? UpdatedAfter = null,
    bool IncludeDeleted = false
) : IRequest<List<ReportDeltaDto>>;

// Shape sent to the offline cache. Deliberately a flat DTO (no navigation
// properties / no DiagnosticReportField children) — the editor only needs
// the narrative payload to render an existing report.
public record ReportDeltaDto(
    Guid Id,
    Guid AppointmentId,
    Guid? DoctorId,
    Guid? TemplateId,
    string Findings,
    string Impression,
    string Advice,
    bool IsFinalized,
    DateTime? FinalizedAt,
    DateTime? CreatedAt,
    DateTime? UpdatedAt,
    DateTime? DeletedAt,
    string ReportingMode,
    // Phase B2 Track 3 — OCC concurrency token. Frontend stores it
    // alongside the cached report and echoes it back on save so the
    // server can detect a stale write.
    byte[]? RowVersion = null
);

public class GetReportsDeltaQueryHandler : IRequestHandler<GetReportsDeltaQuery, List<ReportDeltaDto>>
{
    private readonly IApplicationDbContext _context;

    public GetReportsDeltaQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<ReportDeltaDto>> Handle(GetReportsDeltaQuery request, CancellationToken cancellationToken)
    {
        if (_context.UserContext.HospitalId == Guid.Empty)
        {
            return new List<ReportDeltaDto>();
        }

        var query = _context.DiagnosticReports
            .Where(r => r.HospitalId == _context.UserContext.HospitalId)
            .AsNoTracking();

        if (!request.IncludeDeleted)
        {
            query = query.Where(r => r.DeletedAt == null);
        }

        if (request.UpdatedAfter.HasValue)
        {
            var since = request.UpdatedAfter.Value;
            // UpdatedAt is nullable on this table (legacy rows). The sync
            // index excludes NULLs from range scans naturally; we filter
            // here so the query plan matches.
            query = query.Where(r => r.UpdatedAt != null && r.UpdatedAt > since);
        }

        return await query
            .Select(r => new ReportDeltaDto(
                r.Id,
                r.AppointmentId,
                r.DoctorId,
                r.TemplateId,
                r.Findings,
                r.Impression,
                r.Advice,
                r.IsFinalized,
                r.FinalizedAt,
                r.CreatedAt,
                r.UpdatedAt,
                r.DeletedAt,
                r.ReportingMode,
                r.RowVersion
            ))
            .ToListAsync(cancellationToken);
    }
}
