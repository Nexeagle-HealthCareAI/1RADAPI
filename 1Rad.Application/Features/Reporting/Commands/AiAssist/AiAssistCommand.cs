using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;

namespace _1Rad.Application.Features.Reporting.Commands.AiAssist;

/// <summary>
/// Inline AI co-pilot for the report editor. Transforms a fragment of the
/// report (the radiologist's selection, or the full findings) per a named
/// action and returns clean HTML ready to drop back into the editor.
/// </summary>
public record AiAssistCommand : IRequest<AiAssistResult>
{
    // improve | proofread | expand | shorten | impression
    public string Action { get; init; } = string.Empty;
    // The text/HTML to operate on (the editor selection, or the findings).
    public string Text { get; init; } = string.Empty;
    // Optional free-text context (e.g. study + modality) to steer the model.
    public string? Context { get; init; }
    // When present, the appointment's patient identifiers (name / PTID / phone)
    // are de-identified before the text is sent to the AI provider.
    public Guid? AppointmentId { get; init; }
}

public record AiAssistResult(bool Success, string? Html, string? Error);

public class AiAssistCommandHandler : IRequestHandler<AiAssistCommand, AiAssistResult>
{
    private readonly IReportAiService _ai;
    private readonly IApplicationDbContext _context;

    public AiAssistCommandHandler(IReportAiService ai, IApplicationDbContext context)
    {
        _ai = ai;
        _context = context;
    }

    public async Task<AiAssistResult> Handle(AiAssistCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return new AiAssistResult(false, null, "There is no text to work on. Select some text first.");

        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
        var (system, task) = PromptFor(action);
        if (system == null)
            return new AiAssistResult(false, null, $"Unknown AI action '{request.Action}'.");

        // De-identify before the text leaves our server (rule: no PHI to the
        // provider). Mask the patient's name / PTID / phone, then re-insert them
        // into the model's response so the radiologist still sees real names.
        var phi = new List<string?>();
        if (request.AppointmentId is Guid aid && aid != Guid.Empty)
        {
            var info = await _context.Appointments
                .Where(a => a.AppointmentId == aid)
                .Select(a => new
                {
                    Name = a.PatientName,
                    Ptid = a.Patient.PatientIdentifier,
                    Phone = a.Mobile,
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (info != null) { phi.Add(info.Name); phi.Add(info.Ptid); phi.Add(info.Phone); }
        }
        var (safeText, phiMap) = PhiRedactor.Redact(request.Text.Trim(), phi);

        var user = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.Context))
        {
            user.AppendLine("=== STUDY CONTEXT ===");
            user.AppendLine(request.Context!.Trim());
            user.AppendLine();
        }
        user.AppendLine("=== INPUT ===");
        user.AppendLine(safeText);
        user.AppendLine();
        user.AppendLine(task);

        string aiText;
        try
        {
            aiText = await _ai.GenerateAsync(system, user.ToString(), cancellationToken);
        }
        catch (Exception ex)
        {
            // Fallback: never block report delivery on a third-party API — the
            // caller keeps the raw text and can format manually.
            return new AiAssistResult(false, null, ex.Message);
        }

        var html = PhiRedactor.Restore(Clean(aiText), phiMap);
        if (string.IsNullOrWhiteSpace(html))
            return new AiAssistResult(false, null, "The AI response was empty.");

        return new AiAssistResult(true, html, null);
    }

    // Returns (systemPrompt, perActionInstruction) or (null, null) if unknown.
    private static (string? system, string? task) PromptFor(string action)
    {
        const string baseRules =
            "You are an expert radiologist's editing assistant. You edit fragments of a radiology " +
            "report. Output ONLY the resulting text as simple HTML (use <p>, <strong>, <em>, <ul>, " +
            "<li> where natural — no headings unless the input had them). Never add commentary, " +
            "markdown fences, or explanations. Never invent clinical findings that are not present " +
            "in the input. Preserve laterality (left/right), measurements, and numbers exactly.";

        return action switch
        {
            "improve" => (
                baseRules + " Rewrite the input in clear, concise, professional radiology prose. " +
                "Keep the same meaning and all clinical facts; fix grammar and flow.",
                "Return the improved version of the INPUT."),
            "proofread" => (
                baseRules + " Correct spelling, grammar and punctuation only. Do NOT change wording, " +
                "style, or clinical meaning beyond what is needed for correctness.",
                "Return the corrected version of the INPUT."),
            "expand" => (
                baseRules + " The input is terse shorthand / abbreviations. Expand it into complete, " +
                "grammatical radiology sentences, expanding standard abbreviations, without adding any " +
                "new findings.",
                "Return the expanded full-sentence version of the INPUT."),
            "shorten" => (
                baseRules + " Make the input more concise while keeping every clinical fact, " +
                "measurement and laterality.",
                "Return the shortened version of the INPUT."),
            "polish" => (
                baseRules + " The input is a FULL radiology report draft. Clean it up with a LIGHT touch — do not " +
                "rewrite the radiologist's style:\n" +
                "1. SPELLING & GRAMMAR: correct spelling, grammar and punctuation throughout. Use BRITISH English " +
                "(en-GB) spelling — e.g. haemorrhage, oedema, calibre, visualised, grey, tumour, foetal. Never " +
                "Americanise.\n" +
                "2. STRUCTURE: if the report ALREADY has sections (headings like TECHNIQUE / FINDINGS / IMPRESSION, or " +
                "organ labels such as 'LIVER:', 'KIDNEYS:'), KEEP that exact structure and section order — only tidy the " +
                "wording inside each section and make the EXISTING headings consistent (uppercase, bold, ending with a " +
                "colon). If the report is FREEFORM with no headings, DO NOT add sections or reorder anything — just fix " +
                "the spelling and grammar and leave the radiologist's flow intact.\n" +
                "3. SPACING: single line spacing — one statement per paragraph, and NO empty/blank paragraphs between " +
                "lines.\n" +
                "Keep every clinical fact, measurement, unit and laterality (left/right) exactly as written. Never add, " +
                "remove, or infer findings, and never write or alter an IMPRESSION the radiologist did not write.",
                "Return only the cleaned report as clean HTML using <p> and <strong> (no markdown, no commentary)."),
            "restructure" => (
                baseRules + " The input is a full radiology report draft — possibly unstructured dictation or " +
                "loose notes. Reorganise it into a clean, professional report: a TECHNIQUE/PROTOCOL section only if " +
                "the input mentions one, a FINDINGS section organised by organ/system (one finding per paragraph), " +
                "and a concise numbered IMPRESSION that summarises only the clinically significant findings. Use " +
                "<p><strong>SECTION</strong></p> for each heading. Keep every clinical fact, measurement and " +
                "laterality exactly; never invent findings or an impression unsupported by the findings.",
                "Return the fully restructured report as clean HTML."),
            "impression" => (
                baseRules + " The input is the FINDINGS section of a radiology report. Write a concise, " +
                "numbered IMPRESSION that summarises the clinically significant findings and any " +
                "recommendations. Do not restate normal findings unless clinically relevant. Output an " +
                "ordered list (<ol><li>…</li></ol>) when there are multiple points, otherwise a single <p>.",
                "Return ONLY the impression content (no 'IMPRESSION' heading)."),
            _ => (null, null),
        };
    }

    // Strip markdown fences / stray leading prose, keep the HTML/text body.
    private static string Clean(string aiText)
    {
        if (string.IsNullOrWhiteSpace(aiText)) return string.Empty;
        var text = aiText.Trim();
        var fence = Regex.Match(text, "^```(?:[a-zA-Z]+)?\\s*\\n([\\s\\S]*?)\\n```\\s*$");
        if (fence.Success) text = fence.Groups[1].Value.Trim();
        // Single line spacing: drop empty / whitespace-only paragraphs so the
        // report doesn't come back double-spaced with blank rows between lines.
        text = Regex.Replace(text, @"<p[^>]*>(?:\s|&nbsp;|<br\s*/?>)*</p>", string.Empty, RegexOptions.IgnoreCase);
        return text.Trim();
    }
}
