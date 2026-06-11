using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Assist.Retrain;

/// <summary>
/// "Retrain" helper for RadAI. Reads the captured question log (RadAiQuestionLog),
/// clusters similar questions, ranks them by how often they're asked AND how often
/// the knowledge base failed to cover them, and returns paste-ready DRAFT entries
/// in the app_knowledge.json v1.1 schema for an admin to review and append.
///
/// RadAI is grounded (not trained): "retraining" = growing app_knowledge.json.
/// This query tells you exactly what to add next.
/// </summary>
public record GetRadAiRetrainCandidatesQuery(
    int Days = 30,
    int MaxCandidates = 25,
    int MinCount = 1,
    bool UncoveredOnly = false
) : IRequest<RadAiRetrainResult>;

// A ready-to-paste FAQ stub in the app_knowledge.json "faqs" shape.
public record DraftFaq(string id, string q, string a);

public record RadAiRetrainCandidate(
    string RepresentativeQuestion,
    int Count,
    int UncoveredCount,
    double UncoveredRate,
    List<string> SampleQuestions,
    string SuggestedId,
    DraftFaq DraftFaq);

public record RadAiRetrainResult(
    bool Success,
    int WindowDays,
    int TotalAnswered,
    int TotalUncovered,
    int DistinctQuestions,
    List<RadAiRetrainCandidate> Candidates,
    string? Error);

public class GetRadAiRetrainCandidatesQueryHandler
    : IRequestHandler<GetRadAiRetrainCandidatesQuery, RadAiRetrainResult>
{
    private readonly IApplicationDbContext _db;

    public GetRadAiRetrainCandidatesQueryHandler(IApplicationDbContext db) => _db = db;

    public async Task<RadAiRetrainResult> Handle(GetRadAiRetrainCandidatesQuery q, CancellationToken ct)
    {
        var days = Math.Clamp(q.Days, 1, 365);
        var since = DateTime.UtcNow.AddDays(-days);

        // Scoped to the current centre by the global query filter. Typed
        // questions only — voice rows carry no transcript yet.
        var logs = await _db.RadAiQuestionLogs
            .Where(l => l.CreatedAt >= since && l.Question != null && l.Question != "")
            .Select(l => new { l.Question, l.Covered })
            .ToListAsync(ct);

        var total = logs.Count;
        var totalUncovered = logs.Count(l => !l.Covered);

        // Cluster by a normalised form (case/punctuation-insensitive). This is a
        // first-pass exact-normalised grouping; semantic clustering (embeddings)
        // can replace Normalize() later without changing the response shape.
        var clusters = new Dictionary<string, Cluster>();
        foreach (var l in logs)
        {
            var norm = Normalize(l.Question!);
            if (norm.Length == 0) continue;
            if (!clusters.TryGetValue(norm, out var c))
            {
                c = new Cluster();
                clusters[norm] = c;
            }
            c.Count++;
            if (!l.Covered) c.Uncovered++;
            var raw = l.Question!.Trim();
            c.RawCounts[raw] = c.RawCounts.TryGetValue(raw, out var n) ? n + 1 : 1;
        }

        var ordered = clusters.Values
            .Where(c => c.Count >= Math.Max(1, q.MinCount))
            .Where(c => !q.UncoveredOnly || c.Uncovered > 0)
            // Most-asked first, then most-uncovered, so the biggest gaps float up.
            .OrderByDescending(c => c.Count)
            .ThenByDescending(c => c.Uncovered)
            .Take(Math.Clamp(q.MaxCandidates, 1, 200))
            .ToList();

        var candidates = ordered.Select(c =>
        {
            var rep = c.RawCounts.OrderByDescending(kv => kv.Value).First().Key;
            var samples = c.RawCounts.OrderByDescending(kv => kv.Value)
                                     .Select(kv => kv.Key).Take(5).ToList();
            var id = Slug(rep);
            var rate = c.Count == 0 ? 0 : Math.Round((double)c.Uncovered / c.Count, 2);
            var draftAnswer = c.Uncovered > 0
                ? "DRAFT — RadAI could not answer this from the current knowledge base. Write the real steps from the live app (where to click, what happens), then append this as a faqs[] entry in app_knowledge.json. If it's a whole workflow, add a modules[] entry instead. Keep it plain and end-user focused."
                : "DRAFT — frequently asked but already covered. Review the existing answer; tighten the wording or add keywords so it's retrieved first.";
            return new RadAiRetrainCandidate(
                rep, c.Count, c.Uncovered, rate, samples, id,
                new DraftFaq(id, rep, draftAnswer));
        }).ToList();

        return new RadAiRetrainResult(true, days, total, totalUncovered, clusters.Count, candidates, null);
    }

    private sealed class Cluster
    {
        public int Count;
        public int Uncovered;
        public Dictionary<string, int> RawCounts = new(StringComparer.OrdinalIgnoreCase);
    }

    // Lowercase, strip punctuation, collapse whitespace.
    private static string Normalize(string s)
    {
        s = s.ToLowerInvariant();
        s = Regex.Replace(s, "[^a-z0-9 ]", " ");
        s = Regex.Replace(s, "\\s+", " ").Trim();
        return s;
    }

    // A stable, readable id from the first few words, e.g. "faq_how_do_i_book".
    private static string Slug(string s)
    {
        var words = Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(6);
        var slug = string.Join("_", words);
        return string.IsNullOrEmpty(slug) ? "faq_unnamed" : ("faq_" + slug);
    }
}
