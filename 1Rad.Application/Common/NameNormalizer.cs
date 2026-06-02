using System.Linq;
using System.Text;

namespace _1Rad.Application.Common;

/// <summary>
/// Normalises a person/referrer name for duplicate detection. Mirrors the
/// front-end normalizeName() in utils/duplicateMatch.js so the client hint and
/// the server safety-net agree on what counts as "the same name". Conservative
/// by design: collapses casing/spacing/punctuation/honorific variants only —
/// it does NOT reorder tokens, so genuine namesakes are never auto-merged.
/// </summary>
public static class NameNormalizer
{
    private static readonly string[] Honorifics =
    {
        "dr", "mr", "mrs", "ms", "md", "mohd", "smt", "sri", "shri", "kumari",
        "master", "baby", "mast", "late", "col", "capt", "prof",
    };

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch)) sb.Append(' ');
        }

        var tokens = sb.ToString()
            .Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        // Strip any leading honorifics (can stack: "dr md ...").
        while (tokens.Count > 1 && Honorifics.Contains(tokens[0]))
            tokens.RemoveAt(0);

        return string.Join(' ', tokens);
    }
}
