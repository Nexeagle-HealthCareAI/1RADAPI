namespace _1Rad.Application.Interfaces;

/// <summary>
/// Token usage reported by the model API (real counts, not estimates).
/// CacheReadInputTokens are input tokens served from Anthropic prompt caching
/// (billed at a fraction of the normal input rate); CacheCreationInputTokens are
/// the one-time tokens to write a new cache entry.
/// </summary>
public sealed record AiUsage(
    int InputTokens,
    int OutputTokens,
    int CacheReadInputTokens,
    int CacheCreationInputTokens);

/// <summary>A JSON generation result plus the token usage it billed.</summary>
public sealed record AiJsonResult(string Text, AiUsage Usage);
