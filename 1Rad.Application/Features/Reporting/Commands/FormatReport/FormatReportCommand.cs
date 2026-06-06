using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;

namespace _1Rad.Application.Features.Reporting.Commands.FormatReport;

/// <summary>
/// RadAI report formatter. Takes the radiologist's raw report (editor HTML or
/// plain text) for a known modality/test, de-identifies it, sends it to Gemini
/// with the knowledge pack (template + lexicon + example) for structured output,
/// and returns a draft: the formatted report (as editor HTML) plus the
/// corrections / flags / preserved list for the review UI. Never blocks delivery —
/// on any failure the caller keeps the raw text (or falls back to polish).
/// </summary>
public record FormatReportCommand : IRequest<FormatReportResult>
{
    public string RawText { get; init; } = string.Empty;    // editor HTML or plain text
    public string Modality { get; init; } = string.Empty;   // "USG", "CT", "MRI", "XRAY"
    public string TestCode { get; init; } = string.Empty;   // e.g. "USG_ABDOMEN"
    public string HouseSpelling { get; init; } = "UK";
    public bool AssumeUnmentionedNormal { get; init; }
    // Lets the server de-identify (name/PTID/phone) before the text reaches Gemini.
    public Guid? AppointmentId { get; init; }
}

public record FormatReportCorrection(string From, string To, string Type);
public record FormatReportFlag(string Text, string Issue);

public record FormatReportResult(
    bool Success,
    string? Html,                                   // formatted_report as editor HTML
    string? FormattedText,                          // formatted_report raw text
    List<FormatReportCorrection> Corrections,
    List<FormatReportFlag> Flags,
    List<string> UnchangedProtected,
    string? Error);

public class FormatReportCommandHandler : IRequestHandler<FormatReportCommand, FormatReportResult>
{
    private readonly IReportAiService _ai;
    private readonly IRadiologyPack _pack;
    private readonly IApplicationDbContext _context;

    public FormatReportCommandHandler(IReportAiService ai, IRadiologyPack pack, IApplicationDbContext context)
    {
        _ai = ai;
        _pack = pack;
        _context = context;
    }

    public async Task<FormatReportResult> Handle(FormatReportCommand request, CancellationToken cancellationToken)
    {
        var raw = HtmlToText(request.RawText);
        if (string.IsNullOrWhiteSpace(raw))
            return Fail("There is no report text to format.");
        if (!_pack.IsAvailable || !_pack.HasTest(request.Modality, request.TestCode))
            return Fail($"No formatter template for {request.Modality}/{request.TestCode}.");

        // De-identify before the text leaves our server — same PHI map as the
        // inline co-pilot (patient name / PTID / phone -> placeholders), restored
        // into the model's response so the radiologist still sees the real names.
        var phi = new List<string?>();
        if (request.AppointmentId is Guid aid && aid != Guid.Empty)
        {
            var info = await _context.Appointments
                .Where(a => a.AppointmentId == aid)
                .Select(a => new { Name = a.PatientName, Ptid = a.Patient.PatientIdentifier, Phone = a.Mobile })
                .FirstOrDefaultAsync(cancellationToken);
            if (info != null) { phi.Add(info.Name); phi.Add(info.Ptid); phi.Add(info.Phone); }
        }
        var (safeText, phiMap) = PhiRedactor.Redact(raw, phi);

        var system = _pack.SystemPrompt + "\n\n=== CONTEXT ===\n" + _pack.BuildContext(request.Modality, request.TestCode);
        var userPayload = JsonSerializer.Serialize(new
        {
            modality = request.Modality,
            test_code = request.TestCode,
            house_spelling = string.IsNullOrWhiteSpace(request.HouseSpelling) ? "UK" : request.HouseSpelling,
            assume_unmentioned_normal = request.AssumeUnmentionedNormal,
            raw_text = safeText,
        });

        string modelJson;
        try
        {
            modelJson = await _ai.GenerateJsonAsync(system, userPayload, _pack.BuildResponseSchema(), cancellationToken);
        }
        catch (Exception ex)
        {
            // Fallback: never block report delivery on a third-party API.
            return Fail(ex.Message);
        }

        FormatterPayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<FormatterPayload>(StripFence(modelJson), JsonOpts);
        }
        catch (Exception)
        {
            return Fail("The formatter returned an unreadable response.");
        }
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.FormattedReport))
            return Fail("The formatter returned an empty report.");

        // Restore PHI into everything the radiologist will read.
        var formatted = PhiRedactor.Restore(parsed.FormattedReport, phiMap);
        var corrections = (parsed.Corrections ?? new List<RawCorrection>())
            .Select(c => new FormatReportCorrection(
                PhiRedactor.Restore(c.From, phiMap), PhiRedactor.Restore(c.To, phiMap), c.Type ?? ""))
            .ToList();
        var flags = (parsed.Flags ?? new List<RawFlag>())
            .Select(f => new FormatReportFlag(
                PhiRedactor.Restore(f.Text, phiMap), PhiRedactor.Restore(f.Issue, phiMap)))
            .ToList();
        var protectedList = parsed.UnchangedProtected ?? new List<string>();

        return new FormatReportResult(true, FormattedTextToHtml(formatted), formatted, corrections, flags, protectedList, null);
    }

    private static FormatReportResult Fail(string error) =>
        new(false, null, null, new List<FormatReportCorrection>(), new List<FormatReportFlag>(), new List<string>(), error);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // The model's structured payload (matches output_schema.json).
    private sealed class FormatterPayload
    {
        [JsonPropertyName("formatted_report")] public string FormattedReport { get; set; } = "";
        [JsonPropertyName("corrections")] public List<RawCorrection>? Corrections { get; set; }
        [JsonPropertyName("flags")] public List<RawFlag>? Flags { get; set; }
        [JsonPropertyName("unchanged_protected")] public List<string>? UnchangedProtected { get; set; }
    }
    private sealed class RawCorrection
    {
        [JsonPropertyName("from")] public string From { get; set; } = "";
        [JsonPropertyName("to")] public string To { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
    }
    private sealed class RawFlag
    {
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("issue")] public string Issue { get; set; } = "";
    }

    // ---- helpers ----

    // Strip a ```json fence the model may wrap the JSON in.
    private static string StripFence(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "{}";
        var t = s.Trim();
        var m = Regex.Match(t, "^```(?:[a-zA-Z]+)?\\s*\\n([\\s\\S]*?)\\n```\\s*$");
        return m.Success ? m.Groups[1].Value.Trim() : t;
    }

    // Flatten editor HTML to plain text for the formatter input.
    private static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var t = html;
        t = Regex.Replace(t, "(?i)<li[^>]*>", "\n");
        t = Regex.Replace(t, "(?i)</(p|div|h[1-6]|li|tr)>", "\n");
        t = Regex.Replace(t, "(?i)<br\\s*/?>", "\n");
        t = Regex.Replace(t, "<[^>]+>", string.Empty);            // remaining tags
        t = WebUtility.HtmlDecode(t);
        t = Regex.Replace(t, "[ \\t]+\\n", "\n");
        t = Regex.Replace(t, "\\n{3,}", "\n\n");
        return t.Trim();
    }

    // Convert the formatter's section-headed plain text into editor HTML:
    // ALL-CAPS heading lines -> <p><strong>…</strong></p>, others -> <p>…</p>,
    // blank lines dropped (single spacing).
    private static string FormattedTextToHtml(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder();
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var enc = WebUtility.HtmlEncode(line);
            // Heading: short, mostly uppercase, optionally ending with a colon.
            var isHeading = Regex.IsMatch(line, "^[A-Z0-9][A-Z0-9 /&()\\-]{1,40}:?$") && Regex.IsMatch(line, "[A-Z]");
            sb.Append(isHeading ? $"<p><strong>{enc}</strong></p>" : $"<p>{enc}</p>");
        }
        return sb.ToString();
    }
}
