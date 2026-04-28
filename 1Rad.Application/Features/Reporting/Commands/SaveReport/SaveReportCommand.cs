using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace _1Rad.Application.Features.Reporting.Commands.SaveReport;

public record SaveReportCommand : IRequest<DiagnosticReport>
{
    public string AppointmentId { get; init; } = string.Empty;
    public Guid? TemplateId { get; init; }
    public string Findings { get; init; } = string.Empty;
    public string Impression { get; init; } = string.Empty;
    public string Advice { get; init; } = string.Empty;
    public string ReportingMode { get; init; } = "Structured";
    public bool IsFinalized { get; init; }
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

        _ = Guid.TryParse(request.AppointmentId, out var guidId);
        
        var appointment = await _context.Appointments
            .FirstOrDefaultAsync(a => a.AppointmentId == guidId || a.DisplayId == request.AppointmentId, cancellationToken);
        
        if (appointment == null) throw new KeyNotFoundException($"Appointment '{request.AppointmentId}' not found.");
        if (appointment.HospitalId != hospitalId) throw new UnauthorizedAccessException("Unauthorized context.");

        var report = await _context.DiagnosticReports
            .Include(r => r.Fields)
            .FirstOrDefaultAsync(r => r.AppointmentId == appointment.AppointmentId, cancellationToken);

        if (report == null)
        {
            report = new DiagnosticReport
            {
                Id = Guid.NewGuid(),
                AppointmentId = appointment.AppointmentId,
                DoctorId = doctorId,
                HospitalId = appointment.HospitalId,
                CreatedAt = DateTime.UtcNow
            };
            _context.DiagnosticReports.Add(report);
        }
        else if (report.DoctorId != doctorId)
        {
            throw new UnauthorizedAccessException("Unauthorized modification.");
        }

        // 1. Basic Metadata Update
        report.TemplateId = request.TemplateId;
        report.Findings = request.Findings;
        report.Impression = request.Impression;
        report.Advice = request.Advice;
        report.ReportingMode = request.ReportingMode;
        report.IsFinalized = request.IsFinalized;
        report.FinalizedAt = request.IsFinalized ? DateTime.UtcNow : report.FinalizedAt;

        // 2. Relational Field Shredding (Structured Evolution)
        if (request.ReportingMode == "Structured" && !string.IsNullOrWhiteSpace(request.Findings) && request.Findings.StartsWith("{"))
        {
            // Clear existing fields for fresh sync
            _context.DiagnosticReportFields.RemoveRange(report.Fields);
            report.Fields.Clear();

            try
            {
                var structuredData = JsonSerializer.Deserialize<Dictionary<string, string>>(request.Findings);
                if (structuredData != null)
                {
                    foreach (var kvp in structuredData)
                    {
                        if (string.IsNullOrWhiteSpace(kvp.Value)) continue;
                        
                        report.Fields.Add(new DiagnosticReportField
                        {
                            Id = Guid.NewGuid(),
                            ReportId = report.Id,
                            FieldName = kvp.Key,
                            FieldValue = kvp.Value,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
            }
            catch (JsonException) { /* Fallback to basic findings if parse fails */ }
        }
        else
        {
            // If switched to Narrative, clear structured fields
            if (report.Fields.Any())
            {
                _context.DiagnosticReportFields.RemoveRange(report.Fields);
                report.Fields.Clear();
            }
        }

        report.FieldCount = report.Fields.Count;

        if (request.IsFinalized) appointment.Status = "REPORTED";

        await _context.SaveChangesAsync(cancellationToken);
        return report;
    }
}

