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
    // The report formatter runs on Claude Haiku (IAnthropicService) — text-only
    // structured JSON, no audio — rather than Gemini Flash.
    private readonly IAnthropicService _ai;
    private readonly IRadiologyPack _pack;
    private readonly IRadiologyCorpus _corpus;
    private readonly IApplicationDbContext _context;

    public FormatReportCommandHandler(IAnthropicService ai, IRadiologyPack pack, IRadiologyCorpus corpus, IApplicationDbContext context)
    {
        _ai = ai;
        _pack = pack;
        _corpus = corpus;
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

        // Layer 1 — deterministic spell pass (no AI): fix known typos and obvious
        // misspellings of real radiology terms BEFORE the model runs. Protected
        // tokens (numbers, units, laterality, negations) are never touched.
        var (cleanText, preCorrections) = SpellPass(safeText);

        var system = _pack.SystemPrompt + "\n\n=== CONTEXT ===\n" + _pack.BuildContext(request.Modality, request.TestCode);
        var userPayload = JsonSerializer.Serialize(new
        {
            modality = request.Modality,
            test_code = request.TestCode,
            house_spelling = string.IsNullOrWhiteSpace(request.HouseSpelling) ? "UK" : request.HouseSpelling,
            assume_unmentioned_normal = request.AssumeUnmentionedNormal,
            raw_text = cleanText,
        });

        string modelJson;
        try
        {
            modelJson = await _ai.GenerateJsonAsync(system, userPayload, _pack.BuildResponseSchema(), cancellationToken);
        }
        catch (Exception ex)
        {
            // Fallback: never block report delivery on a third-party API. Turn a
            // transient rate-limit / overload into a clear, actionable message.
            var busy = ex.Message.Contains("429") || ex.Message.Contains("529") || ex.Message.Contains("503");
            return Fail(busy
                ? "The AI formatter is busy right now. Please wait a few seconds and try again."
                : ex.Message);
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
        // Surface the Layer-1 fixes too, so the radiologist reviews every change.
        corrections.InsertRange(0, preCorrections);
        // Layer 2 — whitelist validation: flag any vocabulary "fix" the model made
        // toward a word that isn't a recognised radiology term (a possible
        // hallucination), so the radiologist verifies it before signing.
        var flags = (parsed.Flags ?? new List<RawFlag>())
            .Concat(WhitelistFlags(parsed.Corrections))
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

    // Layer 1 — deterministic spell pass over the de-identified text. Fixes known
    // typos (corrections map) and obvious misspellings of real radiology terms
    // (fuzzy match, edit distance <= 2), skipping protected tokens and the PHI
    // placeholder. Every change is recorded so the radiologist can review it.
    private (string cleaned, List<FormatReportCorrection> corrections) SpellPass(string text)
    {
        var fixes = new List<FormatReportCorrection>();
        if (!_corpus.IsAvailable || string.IsNullOrWhiteSpace(text)) return (text, fixes);

        var cleaned = Regex.Replace(text, "[A-Za-z][A-Za-z-]{2,}", m =>
        {
            var word = m.Value;
            if (string.Equals(word, "PHI", StringComparison.OrdinalIgnoreCase)) return word; // [[PHI0]] redaction
            if (_corpus.IsProtected(word)) return word;

            var known = _corpus.Correction(word);
            if (known != null && !string.Equals(known, word, StringComparison.OrdinalIgnoreCase))
            {
                var to = MatchCase(word, known);
                fixes.Add(new FormatReportCorrection(word, to, "spelling"));
                return to;
            }

            if (!_corpus.IsTerm(word))
            {
                var near = _corpus.NearestTerm(word, 2);
                if (near != null && !string.Equals(near, word, StringComparison.OrdinalIgnoreCase))
                {
                    var to = MatchCase(word, near);
                    fixes.Add(new FormatReportCorrection(word, to, "spelling"));
                    return to;
                }
            }
            return word;
        });
        return (cleaned, fixes);
    }

    // Carry the original word's leading capital onto the replacement.
    private static string MatchCase(string original, string replacement)
    {
        if (original.Length == 0 || replacement.Length == 0) return replacement;
        return char.IsUpper(original[0])
            ? char.ToUpperInvariant(replacement[0]) + replacement.Substring(1)
            : replacement;
    }

    // Layer 2 — whitelist validation. Returns a flag for each vocabulary fix the
    // model reported whose RESULT isn't a recognised radiology term, but whose
    // ORIGINAL clearly was one (a near term). That combination is a likely
    // hallucinated/garbled term — surfaced for review, never silently trusted.
    // General-English fixes (where the original isn't near any radiology term)
    // are deliberately not flagged, so legitimate edits aren't second-guessed.
    private List<RawFlag> WhitelistFlags(List<RawCorrection>? corrections)
    {
        var flags = new List<RawFlag>();
        if (!_corpus.IsAvailable || corrections == null) return flags;
        foreach (var c in corrections)
        {
            var to = (c.To ?? string.Empty).Trim();
            var from = (c.From ?? string.Empty).Trim();
            var type = (c.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (type is not ("spelling" or "abbreviation")) continue;                  // only vocabulary fixes
            if (!Regex.IsMatch(to, "^[A-Za-z][A-Za-z-]+$") || to.Length < 5) continue;  // a single clinical-length word
            if (_corpus.IsTerm(to) || _corpus.IsProtected(to)) continue;                // a real term → trust it
            if (_corpus.NearestTerm(from, 2) == null) continue;                         // original wasn't a radiology word → skip
            flags.Add(new RawFlag
            {
                Text = to,
                Issue = $"AI changed \"{from}\" to \"{to}\", which is not a recognised radiology term — please verify."
            });
        }
        return flags;
    }

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
        var inImpression = false;
        var ulOpen = false;
        void CloseList() { if (ulOpen) { sb.Append("</ul>"); ulOpen = false; } }

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // Section heading: short, mostly uppercase, optionally ending with a colon.
            var isHeading = Regex.IsMatch(line, "^[A-Z0-9][A-Z0-9 /&()\\-]{1,40}:?$") && Regex.IsMatch(line, "[A-Z]");
            if (isHeading)
            {
                CloseList();
                inImpression = line.TrimEnd(':').Trim().StartsWith("IMPRESSION", StringComparison.OrdinalIgnoreCase);
                sb.Append($"<p><strong>{WebUtility.HtmlEncode(line)}</strong></p>");
                continue;
            }

            // IMPRESSION: one clinical point per bullet (strip any leading "1." / "-" / "•").
            if (inImpression)
            {
                var point = Regex.Replace(line, "^\\s*(?:\\d+[.)]|[-*•])\\s*", "").Trim();
                if (point.Length == 0) continue;
                if (!ulOpen) { sb.Append("<ul>"); ulOpen = true; }
                sb.Append($"<li>{WebUtility.HtmlEncode(point)}</li>");
                continue;
            }

            // FINDINGS / other: bold an "Organ: finding" label so each organ/region
            // is visually separated from its finding.
            var m = Regex.Match(line, "^([A-Za-z][A-Za-z0-9 /()\\-]{1,38}?):\\s+(.+)$");
            if (m.Success)
                sb.Append($"<p><strong>{WebUtility.HtmlEncode(m.Groups[1].Value)}:</strong> {WebUtility.HtmlEncode(m.Groups[2].Value)}</p>");
            else
                sb.Append($"<p>{WebUtility.HtmlEncode(line)}</p>");
        }
        CloseList();
        return sb.ToString();
    }
}
