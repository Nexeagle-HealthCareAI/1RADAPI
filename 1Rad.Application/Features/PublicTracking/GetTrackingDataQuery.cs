using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.PublicTracking;

// Tracker-safe view of an appointment for the patient-facing /track page.
// Deliberately omits mobile number, billing fields, technician comments,
// internal delay reasons, latest-comment author, etc. Only what's on the
// printed token slip + status/timestamps the patient cares about.
public record TrackAppointmentDto(
    Guid AppointmentId,
    string DisplayId,
    int? DailyTokenNumber,
    string PatientIdentifier,
    string PatientName,
    string PatientAge,
    string PatientGender,
    string Modality,
    string Service,
    DateTime DateTime,
    string Doctor,
    string ReferredBy,
    string Status,
    string ReportProgressStatus,
    DateTime? ArrivedAt,
    DateTime? ScanStartedAt,
    DateTime? ScannedAt,
    DateTime? DeliveredAt,
    // Multi-service rollout (batch-6 fix). When the visit carries
    // multiple scans (X-ray + CT + USG in one walk-in), the tracking
    // page shows the patient a per-scan progress list instead of just
    // the visit's primary scalar. NULL on responses from a server build
    // that pre-dates this field — the page's fallback render uses the
    // scalar Service / Modality fields above as a single line.
    IReadOnlyList<TrackServiceDto>? Services = null
);

/// <summary>
/// Tracker-safe slice of an AppointmentService. We deliberately omit
/// per-line technician id, internal status timestamps beyond
/// ScanCompletedAt + DeliveredAt, and the per-line referral cut —
/// nothing the patient needs to see beyond "is my CT done yet?".
/// </summary>
public record TrackServiceDto(
    Guid Id,
    string ServiceName,
    string Modality,
    string Status,
    DateTime? ScanCompletedAt,
    DateTime? DeliveredAt
);

// Only included in the response when the report is finalized. We omit the
// raw doctor id, technician comments, and internal metadata. Findings ships
// as-is because the public /track page needs to render the same body.
public record TrackReportDto(
    string ReportingMode,
    string Findings,
    string Impression,
    string Advice,
    bool IsFinalized,
    DateTime? FinalizedAt,
    Guid? DoctorId,
    // Multi-study visits + the sign-off, so the patient page can show each
    // study's signed report with who signed it and any addenda.
    Guid? AppointmentServiceId = null,
    string? ServiceName = null,
    string? SignerName = null,
    string? SignerCredentials = null,
    DateTime? SignedAt = null,
    IReadOnlyList<TrackAddendumDto>? Addenda = null
);

public record TrackAddendumDto(string Text, string AuthorName, string? AuthorCredentials, DateTime SignedAt);

// Patient-safe slice of the doctor's PrescriptionProtocol. This is the same
// branding the patient already sees on their printed prescription: letterhead
// image + typography + page margins. We deliberately exclude internal IDs
// (HospitalId, CreatedAt/UpdatedAt) so a leaked tracking URL can't be used
// to enumerate or correlate doctors / hospitals beyond what's already
// printed on paper in the patient's hand.
public record TrackBrandingDto(
    string? LetterheadBlobUrl,
    string? FontFamily,
    string? FontColor,
    int FontSize,
    decimal HeaderMargin,
    decimal LeftMargin,
    decimal RightMargin,
    decimal BottomMargin,
    string? OverflowBackgroundMode
);

public record TrackingResponseDto(
    TrackAppointmentDto Appointment,
    TrackReportDto? Report,          // the first signed report (older clients)
    TrackBrandingDto? Branding,
    IReadOnlyList<TrackReportDto>? Reports = null   // every signed report on the visit, one per study
);

public record GetTrackingDataQuery(Guid AppointmentId) : IRequest<TrackingResponseDto?>;

public class GetTrackingDataQueryHandler : IRequestHandler<GetTrackingDataQuery, TrackingResponseDto?>
{
    private readonly IApplicationDbContext _context;

    public GetTrackingDataQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<TrackingResponseDto?> Handle(
        GetTrackingDataQuery request,
        CancellationToken cancellationToken)
    {
        // Anonymous endpoint: the caller has no centre, so the global
        // per-centre filter would hide every visit. Access control is the
        // signed token (verified in the controller, bound to this one
        // appointment id), so we bypass the filter and pin every query below
        // to the visit's OWN centre explicitly.
        var hospitalId = await _context.Appointments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.AppointmentId == request.AppointmentId)
            .Select(a => (Guid?)a.HospitalId)
            .FirstOrDefaultAsync(cancellationToken);
        if (hospitalId == null) return null;

        var appt = await _context.Appointments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(a => a.Patient)
            .Where(a => a.AppointmentId == request.AppointmentId && a.HospitalId == hospitalId.Value)
            .Select(a => new TrackAppointmentDto(
                a.AppointmentId,
                a.DisplayId ?? string.Empty,
                a.DailyTokenNumber,
                a.Patient != null ? (a.Patient.PatientIdentifier ?? string.Empty) : string.Empty,
                a.Patient != null ? (a.Patient.FullName ?? "Patient") : "Patient",
                a.Patient != null ? (a.Patient.Age ?? string.Empty) : string.Empty,
                a.Patient != null ? (a.Patient.Gender ?? string.Empty) : string.Empty,
                a.Modality ?? string.Empty,
                a.Service ?? string.Empty,
                a.DateTime,
                a.Doctor ?? string.Empty,
                a.ReferredBy ?? string.Empty,
                a.Status ?? "BOOKED",
                a.ReportProgressStatus ?? "NOT_STARTED",
                a.ArrivedAt,
                a.ScanStartedAt,
                a.ScannedAt,
                a.DeliveredAt,
                // Services materialised separately below — typed null in
                // the EF projection so the expression tree compiles.
                (IReadOnlyList<TrackServiceDto>?)null
            ))
            .FirstOrDefaultAsync(cancellationToken);

        if (appt == null) return null;

        // Multi-service rollout (batch-6 fix). Pull every live service
        // line on this visit so the tracking page can render a per-scan
        // progress list. Tombstones and cancelled lines are filtered
        // out so a patient never sees a "cancelled" row on the public
        // page.
        var services = await _context.AppointmentServices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.AppointmentId == request.AppointmentId
                     && s.HospitalId == hospitalId.Value
                     && s.DeletedAt == null
                     && s.Status != "CANCELLED")
            .OrderBy(s => s.UpdatedAt)
            .Select(s => new TrackServiceDto(
                s.Id,
                s.ServiceName ?? string.Empty,
                s.Modality ?? string.Empty,
                s.Status ?? "NOT_STARTED",
                s.ScanCompletedAt,
                s.DeliveredAt
            ))
            .ToListAsync(cancellationToken);

        appt = appt with { Services = services };

        // Only attach the report body when it's actually finalized. We don't
        // want patients reading half-typed drafts.
        // Signed reports only (Final / Addended), never a draft — one per
        // study on a multi-study visit, in study order.
        var signed = await _context.DiagnosticReports
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.AppointmentId == request.AppointmentId
                     && r.HospitalId == hospitalId.Value
                     && r.DeletedAt == null
                     && r.IsFinalized)
            .OrderBy(r => r.FinalizedAt ?? r.SignedAt ?? r.CreatedAt)
            .Select(r => new
            {
                r.Id,
                Dto = new TrackReportDto(
                    r.ReportingMode ?? "Narrative",
                    r.Findings ?? string.Empty,
                    r.Impression ?? string.Empty,
                    r.Advice ?? string.Empty,
                    r.IsFinalized,
                    r.FinalizedAt,
                    r.DoctorId,
                    r.AppointmentServiceId,
                    null,
                    r.SignerName,
                    r.SignerCredentials,
                    r.SignedAt,
                    null),
            })
            .ToListAsync(cancellationToken);

        var reportIds = signed.Select(x => x.Id).ToList();
        var addenda = reportIds.Count == 0
            ? new List<ReportAddendumRow>()
            : await _context.ReportAddenda
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(a => reportIds.Contains(a.ReportId) && a.HospitalId == hospitalId.Value)
                .OrderBy(a => a.SortOrder)
                .Select(a => new ReportAddendumRow(a.ReportId, a.Text, a.AuthorName, a.AuthorCredentials, a.SignedAt))
                .ToListAsync(cancellationToken);

        var serviceNames = services.ToDictionary(x => x.Id, x => x.ServiceName);
        var serviceOrder = services.Select((x, i) => (x.Id, i)).ToDictionary(t => t.Id, t => t.i);
        var reports = signed
            .Select(x => x.Dto with
            {
                ServiceName = x.Dto.AppointmentServiceId is Guid sid && serviceNames.TryGetValue(sid, out var n) ? n : null,
                Addenda = addenda.Where(a => a.ReportId == x.Id)
                    .Select(a => new TrackAddendumDto(a.Text, a.AuthorName, a.AuthorCredentials, a.SignedAt))
                    .ToList(),
            })
            .OrderBy(r => r.AppointmentServiceId is Guid sid && serviceOrder.TryGetValue(sid, out var i) ? i : int.MaxValue)
            .ToList();
        var report = reports.FirstOrDefault();

        // Branding is only relevant when there's a finalized report to render
        // — the live tracker view uses a flat dark UI, no letterhead. Pulling
        // it conditionally also keeps the response small for cases that don't
        // need it.
        TrackBrandingDto? branding = null;
        if (report != null)
        {
            // The report paper is one setting per centre (migration 79).
            branding = await _context.PrescriptionProtocols
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p => p.HospitalId == hospitalId.Value)
                .OrderByDescending(p => p.LetterheadBlobUrl != null)
                .ThenBy(p => p.CreatedAt)
                .Select(p => new TrackBrandingDto(
                    p.LetterheadBlobUrl,
                    p.FontFamily,
                    p.FontColor,
                    p.FontSize,
                    p.HeaderMargin,
                    p.LeftMargin,
                    p.RightMargin,
                    p.BottomMargin,
                    p.OverflowBackgroundMode
                ))
                .FirstOrDefaultAsync(cancellationToken);
        }

        return new TrackingResponseDto(appt, report, branding, reports);
    }
}

internal sealed record ReportAddendumRow(Guid ReportId, string Text, string AuthorName, string? AuthorCredentials, DateTime SignedAt);
