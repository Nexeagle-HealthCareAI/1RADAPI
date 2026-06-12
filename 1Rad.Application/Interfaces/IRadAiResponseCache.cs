using System;

namespace _1Rad.Application.Interfaces;

/// <summary>
/// A cached RadAI answer plus the token counts the original (uncached) call
/// billed — so a cache HIT can report exactly what it saved.
/// </summary>
public sealed record RadAiCachedAnswer(
    string Answer,
    string ReplyLanguage,
    bool Covered,
    int SavedInputTokens,
    int SavedOutputTokens);

/// <summary>
/// Process-local response cache for RadAI. Keyed on a normalised question +
/// page + language + knowledge version, so repeated questions skip the model
/// entirely (100% token saving on a hit). IMemoryCache-backed singleton today;
/// swap the implementation for Redis when the API scales to multiple instances.
/// </summary>
public interface IRadAiResponseCache
{
    bool TryGet(string key, out RadAiCachedAnswer? answer);
    void Set(string key, RadAiCachedAnswer answer, TimeSpan ttl);
}
