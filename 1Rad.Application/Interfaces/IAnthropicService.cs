namespace _1Rad.Application.Interfaces;

/// <summary>
/// Thin wrapper around the Anthropic (Claude) Messages API. Used by Voice
/// Reporting to turn a radiologist's dictation into a structured report.
/// </summary>
public interface IAnthropicService
{
    /// <summary>True when a real (non-placeholder) API key is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Sends a system + user prompt to Claude and returns the model's text
    /// output. Throws on transport / API errors so callers can surface a
    /// friendly message.
    /// </summary>
    Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <see cref="GenerateAsync"/> but instructs the model to return a
    /// single JSON object (optionally conforming to <paramref name="responseSchema"/>).
    /// Returns the raw JSON text — callers parse it (leniently / strip fences).
    /// </summary>
    Task<string> GenerateJsonAsync(string systemPrompt, string userPrompt, object? responseSchema, CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <see cref="GenerateJsonAsync"/> but also returns the call's token
    /// usage (input/output, plus prompt-cache reads) so callers can attribute and
    /// optimise spend. The system prompt is sent with prompt caching enabled, so
    /// a recurring system prefix is billed at a fraction of the input rate.
    /// </summary>
    Task<AiJsonResult> GenerateJsonWithUsageAsync(string systemPrompt, string userPrompt, object? responseSchema, CancellationToken cancellationToken = default);
}
