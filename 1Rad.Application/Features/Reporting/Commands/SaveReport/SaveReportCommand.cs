using MediatR;
using _1Rad.Application.Common.Exceptions;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.Commands.SaveReport;

public record SaveReportCommand : IRequest<DiagnosticReport>
{
    public string AppointmentId { get; init; } = string.Empty;

    // Cloud PACS-only: report written directly against an ImagingStudy with no
    // visit. Mutually exclusive with AppointmentId — when set, the report is
    // upserted by ImagingStudyId and the appointment/service status rollup is
    // skipped (there is no appointment to advance).
    public Guid? ImagingStudyId { get; init; }
    // Multi-service rollout (step 6). When provided, the upsert is keyed
    // to this specific AppointmentService row so different services on
    // the same visit (X-ray + CT + USG) each get their own report with
    // their own RowVersion — two doctors writing CT and USG reports for
    // the same visit no longer OCC-conflict on the single appointment-
    // level report row. NULL = legacy single-report-per-appointment
    // behaviour (the upsert ignores the column, mirroring v1 clients).
    public Guid? AppointmentServiceId { get; init; }
    public Guid? TemplateId { get; init; }
    public string Findings { get; init; } = string.Empty;
    public string Impression { get; init; } = string.Empty;
    public string Advice { get; init; } = string.Empty;
    public bool IsFinalized { get; init; }
    public string ReportingMode { get; init; } = "Structured";
    // Optimistic-concurrency token (B2 Track 3). Client sends back the
    // RowVersion it received on the last load; absent/empty = "I haven't
    // seen this row yet, skip the check" (e.g. brand-new report).
    public byte[]? RowVersion { get; init; }
}

public class SaveReportCommandHandler : IRequestHandler<SaveReportCommand, DiagnosticReport>
{
    private readonly IApplicationDbContext _context;

    public SaveReportCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<DiagnosticReport> Handle(SaveReportCommand request, CancellationToken cancellationToken)
    {
        var hospitalId = _context.UserContext.HospitalId;
        var doctorId = _context.UserContext.UserId;

        if (string.IsNullOrWhiteSpace(request.Findings))
        {
            throw new ArgumentException("Findings are required.", nameof(request.Findings));
        }

        // A report is owned by exactly one of: an ImagingStudy (Cloud PACS-only)
        // XOR an appointment (RIS / RIS+PACS). Resolve the owner up front and
        // tenant-check it; everything below branches on which one we have.
        Appointment? appointment = null;
        ImagingStudy? study = null;

        if (request.ImagingStudyId is Guid studyId && studyId != Guid.Empty)
        {
            study = await _context.ImagingStudies
                .FirstOrDefaultAsync(s => s.Id == studyId, cancellationToken);
            if (study == null)
            {
                throw new KeyNotFoundException($"Imaging study with ID '{studyId}' not found.");
            }
            if (study.HospitalId != hospitalId)
            {
                throw new UnauthorizedAccessException("You do not have permission to create a report for this study.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.AppointmentId))
            {
                throw new ArgumentException("Either an Appointment ID or an Imaging Study ID is required.", nameof(request.AppointmentId));
            }

            _ = Guid.TryParse(request.AppointmentId, out var guidId);
            appointment = await _context.Appointments
                .FirstOrDefaultAsync(a =>
                    a.AppointmentId == guidId ||
                    a.DisplayId == request.AppointmentId,
                    cancellationToken);

            if (appointment == null)
            {
                throw new KeyNotFoundException($"Appointment with ID '{request.AppointmentId}' not found.");
            }
            if (appointment.HospitalId != hospitalId)
            {
                throw new UnauthorizedAccessException("You do not have permission to create a report for this appointment.");
            }
        }

        var ownerHospitalId = appointment?.HospitalId ?? study!.HospitalId;

        // Upsert keyed by (AppointmentId, AppointmentServiceId). The
        // service-scoped lookup is the multi-service path; the fallback
        // (no AppointmentServiceId in the request OR no per-service
        // report yet) keeps v1 behaviour — find the first existing
        // report on the visit, ignoring service Id. This means an old
        // PWA build that doesn't know about services can still write
        // to the same row it always did.
        DiagnosticReport? report;
        if (study != null)
        {
            // Study-based: one report per study (no service scoping).
            report = await _context.DiagnosticReports
                .Include(r => r.Fields)
                .FirstOrDefaultAsync(r => r.ImagingStudyId == study.Id, cancellationToken);
        }
        else if (request.AppointmentServiceId.HasValue)
        {
            report = await _context.DiagnosticReports
                .Include(r => r.Fields)
                .FirstOrDefaultAsync(r =>
                    r.AppointmentId == appointment!.AppointmentId &&
                    r.AppointmentServiceId == request.AppointmentServiceId,
                    cancellationToken);
        }
        else
        {
            // v1 path: first report on the visit (whichever service it
            // happens to belong to). Preserves single-service behaviour
            // for legacy clients.
            report = await _context.DiagnosticReports
                .Include(r => r.Fields)
                .Where(r => r.AppointmentId == appointment!.AppointmentId)
                .OrderBy(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (report == null)
        {
            // Create new report
            report = new DiagnosticReport
            {
                Id = Guid.NewGuid(),
                AppointmentId = appointment?.AppointmentId,
                ImagingStudyId = study?.Id,
                // Service scoping is an appointment-only concept.
                AppointmentServiceId = study != null ? null : request.AppointmentServiceId,
                DoctorId = doctorId,
                HospitalId = ownerHospitalId,
                TemplateId = request.TemplateId,
                Findings = request.Findings,
                Impression = request.Impression,
                Advice = request.Advice,
                IsFinalized = request.IsFinalized,
                ReportingMode = request.ReportingMode,
                FinalizedAt = request.IsFinalized ? DateTime.UtcNow : null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.DiagnosticReports.Add(report);
        }
        else
        {
            // Update existing report
            if (report.DoctorId != doctorId)
            {
                throw new UnauthorizedAccessException("You do not have permission to modify this report.");
            }

            report.TemplateId = request.TemplateId;
            report.Findings = request.Findings;
            report.Impression = request.Impression;
            report.Advice = request.Advice;
            report.IsFinalized = request.IsFinalized;
            report.ReportingMode = request.ReportingMode;
            report.FinalizedAt = request.IsFinalized ? DateTime.UtcNow : report.FinalizedAt;
            report.UpdatedAt = DateTime.UtcNow;

            // B2 Track 3 — feed the client-supplied token into EF's
            // OriginalValues so the generated UPDATE includes it in WHERE.
            // If the row has moved since the client last read it, EF throws
            // DbUpdateConcurrencyException (handled below). When the client
            // didn't send a token (offline-first first-save, fresh page
            // before delta-pull etc.) we skip — last-write-wins for that
            // unusual case.
            if (request.RowVersion is { Length: > 0 })
            {
                _context.Entry(report).OriginalValues["RowVersion"] = request.RowVersion;
            }
        }

        // --- JSON SHREDDING LOGIC (Relational Evolution) ---
        if (request.ReportingMode == "Structured" && !string.IsNullOrWhiteSpace(request.Findings))
        {
            try 
            {
                var structuredData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(request.Findings);
                if (structuredData != null)
                {
                    // SURGICAL SYNCHRONIZATION: Remove existing fields to avoid collection state confusion
                    if (report.Fields.Any())
                    {
                        _context.DiagnosticReportFields.RemoveRange(report.Fields);
                    }
                    
                    foreach (var field in structuredData)
                    {
                        var newField = new DiagnosticReportField
                        {
                            Id = Guid.NewGuid(),
                            ReportId = report.Id,
                            FieldName = field.Key,
                            FieldValue = field.Value ?? string.Empty,
                            SectionName = "Findings",
                            CreatedAt = DateTime.UtcNow,
                            SortOrder = 0
                        };
                        _context.DiagnosticReportFields.Add(newField);
                    }
                    report.FieldCount = structuredData.Count;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SHREDDING_ERROR] {ex.Message}");
            }
        }

        // If finalized, update the appointment status and the per-service
        // status. When the report is scoped to a specific
        // AppointmentService row (multi-service rollout), bump that row
        // to "REPORTED" too so the workflow + progress badge reflects
        // it. We only set the parent status to "REPORTED" when every
        // live service on the visit has a finalised report — otherwise
        // the worklist would prematurely flip a 1-of-3-reported visit
        // into the green-finalised state.
        // Study-based reports have no visit to advance — the appointment/
        // service status rollup is appointment-only.
        if (request.IsFinalized && appointment != null)
        {
            if (request.AppointmentServiceId.HasValue)
            {
                var thisService = await _context.AppointmentServices
                    .FirstOrDefaultAsync(s =>
                        s.Id == request.AppointmentServiceId.Value &&
                        s.AppointmentId == appointment.AppointmentId,
                        cancellationToken);
                if (thisService != null && thisService.Status != "DELIVERED")
                {
                    thisService.Status = "REPORTED";
                    // ReportedAt anchors the "Awaiting delivery for X" TAT
                    // pill. Stamped on first finalisation; later edits to
                    // the report don't bump it (the patient was already
                    // waiting from the original sign-off).
                    if (thisService.ReportedAt == null) thisService.ReportedAt = DateTime.UtcNow;
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
                // v1 path / single-service visit — same behaviour as before.
                appointment.Status = "REPORTED";
            }
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // OCC mismatch — another writer updated the row between our
            // read and our save. Fetch the canonical current state so the
            // controller can hand it back to the client with 409 Conflict.
            // The frontend uses this to:
            //   1. Overwrite the user's editor with the server's version.
            //   2. Show an "Undo" toast for 30 s — clicking it re-sends
            //      the user's content with the NEW RowVersion so their
            //      edits win deliberately.
            // (Reload as no-tracking on a fresh query so we don't see the
            //  in-flight tracked entity state.)
            // Reload the canonical state for the conflict reply. Match
            // the lookup the upsert used so the OCC body carries the
            // server's view of THIS specific service's report (or the
            // visit-level report when the client didn't scope by
            // service).
            DiagnosticReport? canonical;
            if (study != null)
            {
                canonical = await _context.DiagnosticReports
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.ImagingStudyId == study.Id, cancellationToken);
            }
            else if (request.AppointmentServiceId.HasValue)
            {
                canonical = await _context.DiagnosticReports
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r =>
                        r.AppointmentId == appointment!.AppointmentId &&
                        r.AppointmentServiceId == request.AppointmentServiceId,
                        cancellationToken);
            }
            else
            {
                canonical = await _context.DiagnosticReports
                    .AsNoTracking()
                    .Where(r => r.AppointmentId == appointment!.AppointmentId)
                    .OrderBy(r => r.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
            }
            throw new OccConflictException(canonical!,
                "This report was updated by another user since you opened it.");
        }

        // Break the Fields -> Report cycle for serialization. The principal
        // navigations (Appointment, ImagingStudy, Doctor, Hospital, Template)
        // are all [JsonIgnore], so they don't need nulling here — and nulling
        // them now WOULD be harmful: with these relationships optional, EF
        // relationship fixup would null the matching FK on the returned entity
        // (e.g. clearing ImagingStudy would null ImagingStudyId), corrupting the
        // response the client just saved.
        if (report.Fields != null)
        {
            foreach (var f in report.Fields)
            {
                f.Report = null!;
            }
        }

        return report;
    }
}

