using System.Text;
using System.Text.RegularExpressions;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.Queries.BuildTermFrequency;

/// <summary>
/// Scans this clinic's diagnostic reports and counts how often each RadLex term
/// is ACTUALLY used, so the editor autocomplete can rank the 68k corpus by real
/// usage instead of a heuristic. Output (a term→count map) feeds the frontend
/// generator scripts/build-radlex-corpus.mjs → public/data/radlex_frequency.json.
/// Hospital-scoped (only the caller's reports), soft-deletes excluded.
/// </summary>
public record BuildTermFrequencyQuery(int MaxNgram = 5) : IRequest<Dictionary<string, int>>;

public class BuildTermFrequencyQueryHandler : IRequestHandler<BuildTermFrequencyQuery, Dictionary<string, int>>
{
    private readonly IApplicationDbContext _context;
    private readonly IRadiologyCorpus _corpus;

    public BuildTermFrequencyQueryHandler(IApplicationDbContext context, IRadiologyCorpus corpus)
    {
        _context = context;
        _corpus = corpus;
    }

    public async Task<Dictionary<string, int>> Handle(BuildTermFrequencyQuery request, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!_corpus.IsAvailable) return counts;

        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty) return counts;

        var maxNgram = Math.Clamp(request.MaxNgram, 1, 6);

        // Findings + Impression + Advice are the report's free text. The "." sep
        // stops n-grams from spanning across the three fields.
        var texts = await _context.DiagnosticReports
            .AsNoTracking()
            .Where(r => r.HospitalId == hospitalId && r.DeletedAt == null)
            .Select(r => r.Findings + " . " + r.Impression + " . " + r.Advice)
            .ToListAsync(cancellationToken);

        foreach (var raw in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var words = Tokenize(StripHtml(raw ?? string.Empty));
            for (int i = 0; i < words.Count; i++)
            {
                var sb = new StringBuilder();
                for (int n = 0; n < maxNgram && i + n < words.Count; n++)
                {
                    if (n > 0) sb.Append(' ');
                    sb.Append(words[i + n]);
                    var ngram = sb.ToString();
                    // ≥3 chars filters "of", "a4" noise; IsTerm matches the full
                    // RadLex set incl. multi-word phrases ("ground glass opacity").
                    if (ngram.Length >= 3 && _corpus.IsTerm(ngram))
                        counts[ngram] = counts.TryGetValue(ngram, out var c) ? c + 1 : 1;
                }
            }
        }

        return counts;
    }

    // Findings/Impression are HTML — strip tags + a few common entities so the
    // text tokenizes cleanly. Cheap regex, not a full HTML parse.
    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var t = Regex.Replace(html, "<[^>]+>", " ");
        return t.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<")
                .Replace("&gt;", ">").Replace("&#39;", "'").Replace("&quot;", "\"");
    }

    // Lowercase words, keeping internal hyphens (RadLex has "ground-glass").
    private static List<string> Tokenize(string text)
    {
        var words = new List<string>();
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), "[a-z0-9][a-z0-9-]*"))
        {
            var w = m.Value.Trim('-');
            if (w.Length > 0) words.Add(w);
        }
        return words;
    }
}
