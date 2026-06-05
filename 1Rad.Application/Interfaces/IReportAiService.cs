namespace _1Rad.Application.Interfaces;

/// <summary>
/// Provider-agnostic text-generation service for the report editor's AI
/// co-pilot. Backed by Google Gemini. The API key lives only on the server.
/// </summary>
public interface IReportAiService
{
    /// <summary>True when an API key is configured — lets callers fall back
    /// gracefully (keep the raw text) instead of erroring when AI is off.</summary>
    bool IsConfigured { get; }

    /// <summary>Sends a system + user prompt and returns the model's text.
    /// Throws on transport / API errors so callers can fall back.</summary>
    Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);

    /// <summary>Multimodal variant: a system + user prompt plus optional inline
    /// audio (the RadAI help desk sends the user's spoken question here — Gemini
    /// transcribes and answers in one call). Throws on error so callers fall back.</summary>
    Task<string> GenerateWithAudioAsync(string systemPrompt, string userText, byte[]? audio, string? audioMimeType, CancellationToken cancellationToken = default);
}
