using System.Collections.Generic;

namespace _1Rad.Application.Interfaces;

/// <summary>
/// The radiology term corpus (full RadLex + curated keyword buckets) plus the
/// deterministic wrong→correct map. Loaded once, server-side, from
/// Resources/Radiology. Powers the Layer-1 spell pass (before Haiku), the
/// formatter whitelist, editor autocomplete, and the spell-check underline.
/// Tolerates a missing pack — IsAvailable stays false and callers no-op.
/// </summary>
public interface IRadiologyCorpus
{
    bool IsAvailable { get; }

    /// <summary>True if the (case-insensitive) word is a known radiology term.</summary>
    bool IsTerm(string word);

    /// <summary>Known wrong→correct fix for this exact word, or null.</summary>
    string? Correction(string word);

    /// <summary>
    /// True for tokens that must NEVER be auto-corrected — numbers + units,
    /// laterality (left/right/bilateral), negations (no/not/without/absent),
    /// vertebral levels (L4-L5), MR sequences (T1W/FLAIR), grading (BI-RADS…).
    /// A "corrected" number or a flipped negation is a clinical error.
    /// </summary>
    bool IsProtected(string token);

    /// <summary>Closest real term within <paramref name="maxDistance"/> edits, or null.</summary>
    string? NearestTerm(string word, int maxDistance);

    /// <summary>Up to <paramref name="limit"/> terms that start with the prefix (autocomplete).</summary>
    IReadOnlyList<string> Suggest(string prefix, int limit);
}
