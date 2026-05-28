using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediatR;
using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.Commands.GenerateVoiceReport;

public record GenerateVoiceReportCommand : IRequest<GenerateVoiceReportResult>
{
    public string AppointmentId { get; init; } = string.Empty;
    public Guid? TemplateId { get; init; }
    public string Transcript { get; init; } = string.Empty;
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

        // ── Patient / appointment context ────────────────────────────────
        Guid.TryParse(request.AppointmentId, out var apptGuid);
        var appt = await _context.Appointments
            .Include(a => a.Patient)
            .FirstOrDefaultAsync(a =>
                (apptGuid != Guid.Empty && a.AppointmentId == apptGuid) ||
                a.DisplayId == request.AppointmentId,
                cancellationToken);

        var patientCtx = appt?.Patient != null
            ? $"Patient: {appt.Patient.FullName}; Age: {appt.Patient.Age}; Gender: {appt.Patient.Gender}; Study/Service: {appt.Service}."
            : "Patient context unavailable.";

        // ── Template format (the section scaffold to fill) ───────────────
        string templateFormat = string.Empty;
        if (request.TemplateId.HasValue && request.TemplateId.Value != Guid.Empty)
        {
            var tpl = await _context.ReportTemplates
                .FirstOrDefaultAsync(t => t.Id == request.TemplateId.Value, cancellationToken);
            templateFormat = StripHtml(tpl?.Content ?? string.Empty);
        }

        // ── Prompt ───────────────────────────────────────────────────────
        const string system =
            "You are an expert radiology reporting assistant. You convert a radiologist's spoken dictation " +
            "into a complete, professional, structured radiology report.\n" +
            "You are given a report TEMPLATE describing the required sections (and their normal/default text) " +
            "and the radiologist's DICTATION.\n" +
            "Rules:\n" +
            "1. Follow the template's section structure exactly and in order.\n" +
            "2. For each section, where the dictation describes that organ/finding, use the dictated findings; " +
            "where the dictation does not mention a section, keep the template's normal/default text.\n" +
            "3. Use precise, concise clinical language. Do NOT invent findings not implied by the dictation.\n" +
            "4. End with an IMPRESSION section summarising the key positive findings, if appropriate.\n" +
            "Return ONLY valid JSON — no markdown fences, no commentary. The JSON must be an array of objects " +
            "in report order: [{\"heading\": \"SECTION NAME\", \"text\": \"section content\"}, ...].";

        var user = new StringBuilder();
        user.AppendLine("=== PATIENT CONTEXT ===");
        user.AppendLine(patientCtx);
        user.AppendLine();
        user.AppendLine("=== REPORT TEMPLATE (sections / normal text to follow) ===");
        user.AppendLine(string.IsNullOrWhiteSpace(templateFormat)
            ? "(No template provided — produce a standard structured report appropriate for the service.)"
            : templateFormat);
        user.AppendLine();
        user.AppendLine("=== RADIOLOGIST DICTATION ===");
        user.AppendLine(request.Transcript.Trim());

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

        var html = SectionsJsonToHtml(aiText);
        if (string.IsNullOrWhiteSpace(html))
            return new GenerateVoiceReportResult(false, null, "The AI response could not be parsed into a report.");

        return new GenerateVoiceReportResult(true, html, null);
    }

    private static string StripHtml(string s) =>
        string.IsNullOrEmpty(s) ? string.Empty : Regex.Replace(s, "<[^>]+>", " ").Trim();

    /// <summary>
    /// Parse the model's JSON section array and assemble editor-friendly HTML:
    /// each section becomes a bold heading paragraph + a body paragraph.
    /// </summary>
    private static string SectionsJsonToHtml(string aiText)
    {
        var json = ExtractJsonArray(aiText);
        if (json == null) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return string.Empty;

            var sb = new StringBuilder();
            foreach (var section in doc.RootElement.EnumerateArray())
            {
                var heading = section.TryGetProperty("heading", out var h) ? h.GetString() : null;
                var text = section.TryGetProperty("text", out var t) ? t.GetString() : null;
                if (!string.IsNullOrWhiteSpace(heading))
                    sb.Append($"<p><strong>{Esc(heading!.Trim())}</strong></p>");
                if (!string.IsNullOrWhiteSpace(text))
                    sb.Append($"<p>{Esc(text!.Trim()).Replace("\n", "<br/>")}</p>");
            }
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Pull the first top-level JSON array out of the model text (tolerates stray prose / code fences).</summary>
    private static string? ExtractJsonArray(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        return (start >= 0 && end > start) ? text.Substring(start, end - start + 1) : null;
    }

    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
}
