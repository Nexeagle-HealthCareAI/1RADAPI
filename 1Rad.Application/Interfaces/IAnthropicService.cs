namespace _1Rad.Application.Interfaces;

/// <summary>
/// Thin wrapper around the Anthropic (Claude) Messages API. Used by Voice
/// Reporting to turn a radiologist's dictation into a structured report.
/// </summary>
public interface IAnthropicService
{
    /// <summary>
    /// Sends a system + user prompt to Claude and returns the model's text
    /// output. Throws on transport / API errors so callers can surface a
    /// friendly message.
    /// </summary>
    Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);
}
