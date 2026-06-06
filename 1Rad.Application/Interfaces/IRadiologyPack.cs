namespace _1Rad.Application.Interfaces;

/// <summary>
/// Loads the radiology knowledge pack (system prompt + per-modality templates +
/// lexicon + few-shot examples shipped under Resources/Radiology) and assembles
/// the per-call context block for the report formatter. Pure data — no network.
/// </summary>
public interface IRadiologyPack
{
    /// <summary>True when the pack files loaded successfully. When false (pack
    /// missing / unreadable), callers fall back to the generic "polish" action.</summary>
    bool IsAvailable { get; }

    /// <summary>The formatter system prompt (system_prompt.md).</summary>
    string SystemPrompt { get; }

    /// <summary>True when a template exists for this modality + test_code.</summary>
    bool HasTest(string modality, string testCode);

    /// <summary>Template + lexicon + one matching few-shot example, as a single
    /// text block to append after the system prompt.</summary>
    string BuildContext(string modality, string testCode);

    /// <summary>Gemini-compatible responseSchema (OpenAPI subset) for the
    /// formatter output. Passed to the structured generation call.</summary>
    object BuildResponseSchema();
}
