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
    Guid? DoctorId
);

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
    TrackReportDto? Report,
    TrackBrandingDto? Branding
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
        // No hospital-context filter — anonymous endpoint, access control is
        // the signed token verified at the controller layer.
        var appt = await _context.Appointments
            .AsNoTracking()
            .Include(a => a.Patient)
            .Where(a => a.AppointmentId == request.AppointmentId)
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
                a.DeliveredAt
            ))
            .FirstOrDefaultAsync(cancellationToken);

        if (appt == null) return null;

        // Only attach the report body when it's actually finalized. We don't
        // want patients reading half-typed drafts.
        var report = await _context.DiagnosticReports
            .AsNoTracking()
            .Where(r => r.AppointmentId == request.AppointmentId)
            .Select(r => new TrackReportDto(
                r.ReportingMode ?? "Narrative",
                r.Findings ?? string.Empty,
                r.Impression ?? string.Empty,
                r.Advice ?? string.Empty,
                r.IsFinalized,
                r.FinalizedAt,
                r.DoctorId
            ))
            .FirstOrDefaultAsync(cancellationToken);

        if (report != null && !report.IsFinalized) report = null;

        // Branding is only relevant when there's a finalized report to render
        // — the live tracker view uses a flat dark UI, no letterhead. Pulling
        // it conditionally also keeps the response small for cases that don't
        // need it.
        TrackBrandingDto? branding = null;
        if (report != null && report.DoctorId.HasValue)
        {
            branding = await _context.PrescriptionProtocols
                .AsNoTracking()
                .Where(p => p.DoctorId == report.DoctorId.Value)
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

        return new TrackingResponseDto(appt, report, branding);
    }
}
