using MediatR;
using _1Rad.Application.Common;
using _1Rad.Application.Common.Exceptions;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.Commands.FinalizeReport;

/// <summary>
/// Electronically signs a report (21 CFR Part 11). Distinct from SaveReport:
/// the radiologist re-authenticates with their password, the signature is bound
/// to their authenticated identity, the content is hashed and (for Final) locked,
/// and a tamper-evident audit event is appended. TargetStatus selects a
/// "Preliminary" (wet read) or "Final" sign-off.
/// </summary>
public record FinalizeReportCommand : IRequest<DiagnosticReport>
{
    // Report owner — exactly one of AppointmentId / ImagingStudyId, mirroring
    // SaveReport. AppointmentServiceId scopes multi-service visits.
    public string AppointmentId { get; init; } = string.Empty;
    public Guid? ImagingStudyId { get; init; }
    public Guid? AppointmentServiceId { get; init; }

    // "Preliminary" | "Final".
    public string TargetStatus { get; init; } = ReportStatuses.Final;

    // The signer's account password — the meaning-of-signature re-auth.
    public string Password { get; init; } = string.Empty;

    // Optional credentials snapshot for the signature block (e.g. "MD, FRCR").
    // When omitted we fall back to the user's profile (Degree / License).
    public string? Credentials { get; init; }

    // OCC token — the version the client last saw. Guarantees we sign exactly
    // the content the radiologist reviewed, not a racing concurrent edit.
    public byte[]? RowVersion { get; init; }
}

public class FinalizeReportCommandHandler : IRequestHandler<FinalizeReportCommand, DiagnosticReport>
{
    private readonly IApplicationDbContext _context;
    private readonly IPasswordHasher _passwordHasher;

    public FinalizeReportCommandHandler(IApplicationDbContext context, IPasswordHasher passwordHasher)
    {
        _context = context;
        _passwordHasher = passwordHasher;
    }

    public async Task<DiagnosticReport> Handle(FinalizeReportCommand request, CancellationToken cancellationToken)
    {
        var hospitalId = _context.UserContext.HospitalId;
        var doctorId = _context.UserContext.UserId;

        var target = (request.TargetStatus ?? string.Empty).Trim();
        if (target != ReportStatuses.Preliminary && target != ReportStatuses.Final)
        {
            throw new ArgumentException(
                "TargetStatus must be 'Preliminary' or 'Final'.", nameof(request.TargetStatus));
        }

        // ── Re-authenticate: the signature's meaning is bound to a fresh
        // password check, not just the active session (Part 11 §11.200). ──
        var user = await _context.Users.FirstOrDefaultAsync(u => u.UserId == doctorId, cancellationToken);
        if (user == null)
        {
            throw new UnauthorizedAccessException("Signing user not found.");
        }
        if (string.IsNullOrEmpty(user.PasswordHash) || !_passwordHasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
        {
            throw new UnauthorizedAccessException("Password verification failed — your signature was not applied.");
        }

        // ── Resolve the report owner (appointment XOR study), tenant-checked. ──
        Appointment? appointment = null;
        ImagingStudy? study = null;

        if (request.ImagingStudyId is Guid studyId && studyId != Guid.Empty)
        {
            study = await _context.ImagingStudies.FirstOrDefaultAsync(s => s.Id == studyId, cancellationToken);
            if (study == null) throw new KeyNotFoundException($"Imaging study with ID '{studyId}' not found.");
            if (study.HospitalId != hospitalId)
                throw new UnauthorizedAccessException("You do not have permission to sign a report for this study.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.AppointmentId))
                throw new ArgumentException("Either an Appointment ID or an Imaging Study ID is required.", nameof(request.AppointmentId));

            _ = Guid.TryParse(request.AppointmentId, out var guidId);
            appointment = await _context.Appointments
                .FirstOrDefaultAsync(a => a.AppointmentId == guidId || a.DisplayId == request.AppointmentId, cancellationToken);
            if (appointment == null) throw new KeyNotFoundException($"Appointment with ID '{request.AppointmentId}' not found.");
            if (appointment.HospitalId != hospitalId)
                throw new UnauthorizedAccessException("You do not have permission to sign a report for this appointment.");
        }

        // ── Load the report to be signed (must already be saved). ──
        DiagnosticReport? report;
        if (study != null)
        {
            report = await _context.DiagnosticReports
                .FirstOrDefaultAsync(r => r.ImagingStudyId == study.Id, cancellationToken);
        }
        else if (request.AppointmentServiceId.HasValue)
        {
            report = await _context.DiagnosticReports
                .FirstOrDefaultAsync(r => r.AppointmentId == appointment!.AppointmentId
                                       && r.AppointmentServiceId == request.AppointmentServiceId, cancellationToken);
        }
        else
        {
            report = await _context.DiagnosticReports
                .Where(r => r.AppointmentId == appointment!.AppointmentId)
                .OrderBy(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (report == null)
            throw new KeyNotFoundException("There is no saved report to sign. Save the report before finalising.");

        // Only the authoring radiologist may sign their report. (DoctorId is
        // stamped on the draft; an unowned legacy draft is claimed by the signer.)
        if (report.DoctorId.HasValue && report.DoctorId.Value != doctorId)
            throw new UnauthorizedAccessException("You do not have permission to sign this report.");

        // A Final/Addended report is immutable — re-signing isn't allowed
        // (corrections go through an addendum).
        if (ReportStatuses.IsLocked(report.Status))
            throw new ReportLockedException("This report is already finalised. Add an addendum to make a correction.");

        var nowUtc = DateTime.UtcNow;
        var contentHash = ReportHashing.HashContent(report.Findings, report.Impression, report.Advice);

        // ── Apply the signature. ──
        if (report.DoctorId == null) report.DoctorId = doctorId;
        report.Status = target;
        report.SignedByUserId = doctorId;
        report.SignerName = user.FullName;
        report.SignerCredentials = string.IsNullOrWhiteSpace(request.Credentials)
            ? BuildCredentials(user)
            : request.Credentials!.Trim();
        report.SignedAt = nowUtc;
        report.SignedContentHash = contentHash;
        report.UpdatedAt = nowUtc;

        if (target == ReportStatuses.Final)
        {
            report.IsFinalized = true;
            report.FinalizedAt = nowUtc;
        }
        else
        {
            // Preliminary is signed but NOT final — keep IsFinalized false so the
            // worklist + downstream consumers don't treat it as a closed report.
            report.IsFinalized = false;
        }

        // ── Tamper-evident audit event, chained to the prior event. ──
        var prevHash = await _context.ReportAuditEvents
            .Where(e => e.ReportId == report.Id)
            .OrderByDescending(e => e.Timestamp)
            .Select(e => e.ContentHash)
            .FirstOrDefaultAsync(cancellationToken);

        _context.ReportAuditEvents.Add(new ReportAuditEvent
        {
            Id = Guid.NewGuid(),
            ReportId = report.Id,
            HospitalId = report.HospitalId,
            EventType = target == ReportStatuses.Final
                ? ReportAuditEventTypes.SignedFinal
                : ReportAuditEventTypes.SignedPreliminary,
            ActorUserId = doctorId,
            ActorName = user.FullName,
            Timestamp = nowUtc,
            ContentHash = contentHash,
            PreviousHash = prevHash,
        });

        // OCC: sign exactly the version the radiologist reviewed.
        if (request.RowVersion is { Length: > 0 })
        {
            _context.Entry(report).OriginalValues["RowVersion"] = request.RowVersion;
        }

        // ── Appointment / per-service "REPORTED" rollup — only on FINAL, and
        // only for appointment-linked reports (study-based reports have no
        // visit to advance). Moved here from SaveReport. ──
        if (target == ReportStatuses.Final && appointment != null)
        {
            if (request.AppointmentServiceId.HasValue)
            {
                var thisService = await _context.AppointmentServices
                    .FirstOrDefaultAsync(s => s.Id == request.AppointmentServiceId.Value
                                           && s.AppointmentId == appointment.AppointmentId, cancellationToken);
                if (thisService != null && thisService.Status != "DELIVERED")
                {
                    thisService.Status = "REPORTED";
                    if (thisService.ReportedAt == null) thisService.ReportedAt = nowUtc;
                    if (thisService.ScanCompletedAt == null) thisService.ScanCompletedAt = thisService.ReportedAt;
                }

                var liveSiblings = await _context.AppointmentServices
                    .Where(s => s.AppointmentId == appointment.AppointmentId
                             && s.DeletedAt == null
                             && s.Id != request.AppointmentServiceId.Value)
                    .Select(s => s.Id)
                    .ToListAsync(cancellationToken);

                if (liveSiblings.Count == 0)
                {
                    appointment.Status = "REPORTED";
                }
                else
                {
                    var siblingFinalisedCount = await _context.DiagnosticReports
                        .Where(r => r.AppointmentId == appointment.AppointmentId
                                 && r.AppointmentServiceId.HasValue
                                 && liveSiblings.Contains(r.AppointmentServiceId.Value)
                                 && r.IsFinalized)
                        .CountAsync(cancellationToken);
                    if (siblingFinalisedCount == liveSiblings.Count)
                    {
                        appointment.Status = "REPORTED";
                    }
                }
            }
            else
            {
                appointment.Status = "REPORTED";
            }
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            DiagnosticReport? canonical;
            if (study != null)
            {
                canonical = await _context.DiagnosticReports.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.ImagingStudyId == study.Id, cancellationToken);
            }
            else if (request.AppointmentServiceId.HasValue)
            {
                canonical = await _context.DiagnosticReports.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.AppointmentId == appointment!.AppointmentId
                                           && r.AppointmentServiceId == request.AppointmentServiceId, cancellationToken);
            }
            else
            {
                canonical = await _context.DiagnosticReports.AsNoTracking()
                    .Where(r => r.AppointmentId == appointment!.AppointmentId)
                    .OrderBy(r => r.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
            }
            throw new OccConflictException(canonical!,
                "This report changed since you opened it — review the latest version before signing.");
        }

        return report;
    }

    private static string BuildCredentials(User user)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(user.Degree)) parts.Add(user.Degree!.Trim());
        if (!string.IsNullOrWhiteSpace(user.LicenseNo)) parts.Add($"Lic. {user.LicenseNo!.Trim()}");
        return string.Join(", ", parts);
    }
}
