namespace _1Rad.Application.Interfaces;

/// <summary>
/// Loads the RadAI help-desk knowledge pack (assistant_system_prompt.md +
/// app_knowledge.json under Resources/RadAI) once and exposes it for the in-app
/// assistant. The assistant answers ONLY from this grounding file. Pure data.
/// </summary>
public interface IRadAiKnowledge
{
    /// <summary>True when the pack loaded. When false the handler returns a
    /// friendly "not switched on" message rather than guessing.</summary>
    bool IsAvailable { get; }

    /// <summary>Full system instruction: the assistant prompt + the app knowledge
    /// base appended, optionally hinting the user's current screen.</summary>
    string BuildSystemPrompt(string? page);

    /// <summary>Gemini-compatible responseSchema for the structured answer
    /// (answer, reply_language, suggested_followups, covered).</summary>
    object BuildResponseSchema();
}
