using MediatR;
using _1Rad.Application.Common.Exceptions;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.Commands.SaveReport;

public record SaveReportCommand : IRequest<DiagnosticReport>
{
    public string AppointmentId { get; init; } = string.Empty;
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

        // Validate required fields
        if (string.IsNullOrWhiteSpace(request.AppointmentId))
        {
            throw new ArgumentException("Appointment ID is required.", nameof(request.AppointmentId));
        }

        if (string.IsNullOrWhiteSpace(request.Findings))
        {
            throw new ArgumentException("Findings are required.", nameof(request.Findings));
        }

        _ = Guid.TryParse(request.AppointmentId, out var guidId);
        
        // Fetch the appointment to ensure correct context (HospitalId)
        var appointment = await _context.Appointments
            .FirstOrDefaultAsync(a => 
                a.AppointmentId == guidId || 
                a.DisplayId == request.AppointmentId, 
                cancellationToken);
        
        if (appointment == null)
        {
            throw new KeyNotFoundException($"Appointment with ID '{request.AppointmentId}' not found.");
        }

        // Verify hospital context
        if (appointment.HospitalId != hospitalId)
        {
            throw new UnauthorizedAccessException("You do not have permission to create a report for this appointment.");
        }

        var report = await _context.DiagnosticReports
            .Include(r => r.Fields)
            .FirstOrDefaultAsync(r => r.AppointmentId == appointment.AppointmentId, cancellationToken);

        if (report == null)
        {
            // Create new report
            report = new DiagnosticReport
            {
                Id = Guid.NewGuid(),
                AppointmentId = appointment.AppointmentId,
                DoctorId = doctorId,
                HospitalId = appointment.HospitalId,
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

        // If finalized, update the appointment status
        if (request.IsFinalized)
        {
            appointment.Status = "REPORTED";
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
            var canonical = await _context.DiagnosticReports
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.AppointmentId == appointment.AppointmentId, cancellationToken);
            throw new OccConflictException(canonical!,
                "This report was updated by another user since you opened it.");
        }

        // TACTICAL: Break circular references for serialization without relying on attributes
        report.Appointment = null!;
        report.Doctor = null!;
        report.Hospital = null!;
        report.Template = null!;
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

