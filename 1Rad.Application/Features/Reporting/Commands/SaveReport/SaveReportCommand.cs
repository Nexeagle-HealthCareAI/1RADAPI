using MediatR;
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
                CreatedAt = DateTime.UtcNow
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
        catch (DbUpdateConcurrencyException ex)
        {
            // Log details for debugging
            foreach (var entry in ex.Entries)
            {
                var entityName = entry.Entity.GetType().Name;
                System.Diagnostics.Debug.WriteLine($"[CONCURRENCY_ERROR] Entity: {entityName}, State: {entry.State}");
            }
            throw; // Re-throw to inform the user/caller
        }

        return report;
    }
}

