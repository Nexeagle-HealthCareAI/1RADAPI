using System.Text;
using System.Text.RegularExpressions;
using MediatR;
using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.Commands.GenerateVoiceReport;

public record GenerateVoiceReportCommand : IRequest<GenerateVoiceReportResult>
{
    public string? AppointmentId { get; init; }
    // Multi-service rollout (batch-3 fix). When supplied, the dictation
    // context handed to Claude Haiku names this specific service line
    // — so a CT report dictation isn't seeded with the X-ray service
    // name from the visit's primary. NULL = legacy / single-service
    // path: handler falls back to the parent appointment's scalar
    // Service + Modality (same as today).
    public Guid? AppointmentServiceId { get; init; }
    public Guid? TemplateId { get; init; }
    public string Transcript { get; init; } = string.Empty;

    // PACS-only: dictate against an ImagingStudy with no visit. When set, the
    // dictation context is built from the study's denormalized demographics.
    public Guid? ImagingStudyId { get; init; }
}

public record GenerateVoiceReportResult(bool Success, string? Html, string? Error);

public class GenerateVoiceReportCommandHandler
    : IRequestHandler<GenerateVoiceReportCommand, GenerateVoiceReportResult>
{
    private readonly IApplicationDbContext _context;
    private readonly IAnthropicService _ai;

    public GenerateVoiceReportCommandHandler(IApplicationDbContext context, IAnthropicService ai)
    {
        _context = context;
        _ai = ai;
    }

    public async Task<GenerateVoiceReportResult> Handle(GenerateVoiceReportCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Transcript))
            return new GenerateVoiceReportResult(false, null, "Dictation transcript is empty.");

        // ── Patient context — from the study (PACS-only) or the appointment ─
        string patientCtx;
        if (request.ImagingStudyId is Guid studyId && studyId != Guid.Empty)
        {
            // PACS-only: no visit; context comes from the study's demographics.
            var study = await _context.ImagingStudies
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == studyId, cancellationToken);
            patientCtx = study != null
                ? $"Patient: {study.PatientName}; Study/Service: {study.StudyDescription} ({study.Modality})."
                : "Patient context unavailable.";
        }
        else
        {
            Guid.TryParse(request.AppointmentId, out var apptGuid);
            var appt = await _context.Appointments
                .Include(a => a.Patient)
                .FirstOrDefaultAsync(a =>
                    (apptGuid != Guid.Empty && a.AppointmentId == apptGuid) ||
                    a.DisplayId == request.AppointmentId,
                    cancellationToken);

            // Service-scoped context. When the caller passed an
            // AppointmentServiceId, name THAT service in the dictation
            // context — so a CT report dictation reads
            // "Study/Service: CT Head Plain (CT)" rather than the visit's
            // primary X-ray scalar. Falls back to the parent's scalar
            // fields when no service id is supplied (single-service /
            // legacy / v1 client).
            string studyLabel = appt?.Service ?? string.Empty;
            string modalityLabel = appt?.Modality ?? string.Empty;
            if (appt != null && request.AppointmentServiceId.HasValue)
            {
                var svc = await _context.AppointmentServices
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s =>
                        s.Id == request.AppointmentServiceId.Value &&
                        s.AppointmentId == appt.AppointmentId,
                        cancellationToken);
                if (svc != null)
                {
                    studyLabel    = svc.ServiceName ?? studyLabel;
                    modalityLabel = svc.Modality    ?? modalityLabel;
                }
            }

            patientCtx = appt?.Patient != null
                ? $"Patient: {appt.Patient.FullName}; Age: {appt.Patient.Age}; Gender: {appt.Patient.Gender}; Study/Service: {studyLabel} ({modalityLabel})."
                : "Patient context unavailable.";
        }

        // ── Template HTML (the EXACT structure to preserve) ──────────────
        // We pass the template's HTML through to Haiku unchanged so the
        // response keeps the same tags, classes, inline styles, headings, and
        // layout. The final HTML is printed and handed to the patient, so
        // visual fidelity to the prescription template is non-negotiable.
        string templateHtml = string.Empty;
        if (request.TemplateId.HasValue && request.TemplateId.Value != Guid.Empty)
        {
            var tpl = await _context.ReportTemplates
                .FirstOrDefaultAsync(t => t.Id == request.TemplateId.Value, cancellationToken);
            templateHtml = tpl?.Content ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(templateHtml))
            return new GenerateVoiceReportResult(false, null, "No report template was provided. Please select a template.");

        // ── Prompt ───────────────────────────────────────────────────────
        const string system =
            "You are an expert radiology reporting assistant. You convert a radiologist's spoken dictation " +
            "into a complete, professional, structured radiology report.\n" +
            "\n" +
            "You are given:\n" +
            "  • A report TEMPLATE — raw HTML that defines the exact print layout (headings, sections, " +
            "    inline styles, tables, etc.) of the prescription handed to the patient.\n" +
            "  • The radiologist's DICTATION.\n" +
            "\n" +
            "Your job — return the SAME HTML, with the section contents filled in or updated to reflect " +
            "the dictation. Output is rendered directly to the printed report, so structural fidelity is " +
            "non-negotiable.\n" +
            "\n" +
            "STRICT RULES:\n" +
            "1. PRESERVE the template's HTML structure exactly: every tag, attribute, class, inline style, " +
            "   table, heading, ordering, and whitespace pattern. Do not add or remove elements.\n" +
            "2. Modify ONLY the TEXT CONTENT inside the existing elements. Do not introduce new tags, " +
            "   change tag names, or restructure the document.\n" +
            "3. For each section where the dictation describes that organ/finding, replace the template's " +
            "   default text with the dictated findings, written in concise clinical language.\n" +
            "4. For sections the dictation does NOT mention, keep the template's existing normal text as-is.\n" +
            "5. NEVER invent findings that are not implied by the dictation. NEVER add commentary, " +
            "   markdown fences, code blocks, or explanations.\n" +
            "6. If the template includes patient-info placeholders (e.g. {patientName}), leave them " +
            "   untouched — the editor substitutes them at render time.\n" +
            "7. Return ONLY the completed HTML. No prose before or after.";

        var user = new StringBuilder();
        user.AppendLine("=== PATIENT CONTEXT ===");
        user.AppendLine(patientCtx);
        user.AppendLine();
        user.AppendLine("=== REPORT TEMPLATE (HTML — preserve this exact structure) ===");
        user.AppendLine(templateHtml);
        user.AppendLine();
        user.AppendLine("=== RADIOLOGIST DICTATION ===");
        user.AppendLine(request.Transcript.Trim());
        user.AppendLine();
        user.AppendLine("Return the completed HTML, preserving every tag and style from the template.");

        // ── Call Claude ───────────────────────────────────────────────────
        string aiText;
        try
        {
            aiText = await _ai.GenerateAsync(system, user.ToString(), cancellationToken);
        }
        catch (Exception ex)
        {
            return new GenerateVoiceReportResult(false, null, ex.Message);
        }

        var html = ExtractHtml(aiText);
        if (string.IsNullOrWhiteSpace(html))
            return new GenerateVoiceReportResult(false, null, "The AI response did not contain a valid HTML report.");

        return new GenerateVoiceReportResult(true, html, null);
    }

    /// <summary>
    /// Strip markdown code fences (```html … ```) and surrounding whitespace
    /// if Haiku wraps the response despite the prompt. Returns the inner HTML.
    /// </summary>
    private static string ExtractHtml(string aiText)
    {
        if (string.IsNullOrWhiteSpace(aiText)) return string.Empty;
        var text = aiText.Trim();

        // ```html\n…\n``` or ```\n…\n```
        var fence = Regex.Match(text, "^```(?:[a-zA-Z]+)?\\s*\\n([\\s\\S]*?)\\n```\\s*$");
        if (fence.Success) text = fence.Groups[1].Value.Trim();

        // Drop any stray leading prose before the first '<' tag (rare, but
        // defensive — keeps a stray "Here is the report:" out of the print).
        var firstTag = text.IndexOf('<');
        if (firstTag > 0) text = text.Substring(firstTag).Trim();

        return text;
    }
}
