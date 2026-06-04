using System.Text;
using System.Text.RegularExpressions;
using MediatR;
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
}

public record AiAssistResult(bool Success, string? Html, string? Error);

public class AiAssistCommandHandler : IRequestHandler<AiAssistCommand, AiAssistResult>
{
    private readonly IAnthropicService _ai;

    public AiAssistCommandHandler(IAnthropicService ai)
    {
        _ai = ai;
    }

    public async Task<AiAssistResult> Handle(AiAssistCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return new AiAssistResult(false, null, "There is no text to work on. Select some text first.");

        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
        var (system, task) = PromptFor(action);
        if (system == null)
            return new AiAssistResult(false, null, $"Unknown AI action '{request.Action}'.");

        var user = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.Context))
        {
            user.AppendLine("=== STUDY CONTEXT ===");
            user.AppendLine(request.Context!.Trim());
            user.AppendLine();
        }
        user.AppendLine("=== INPUT ===");
        user.AppendLine(request.Text.Trim());
        user.AppendLine();
        user.AppendLine(task);

        string aiText;
        try
        {
            aiText = await _ai.GenerateAsync(system, user.ToString(), cancellationToken);
        }
        catch (Exception ex)
        {
            return new AiAssistResult(false, null, ex.Message);
        }

        var html = Clean(aiText);
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
        return text;
    }
}
