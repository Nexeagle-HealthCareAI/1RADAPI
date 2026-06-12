using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Assist.Usage;

/// <summary>
/// RadAI token-usage &amp; savings dashboard. Aggregates the question log to show
/// what RadAI is billing and how much the two optimizations — response caching
/// (repeats skip the model) and prompt caching (the static system prompt billed
/// at ~10%) — are saving, as a before/after. Scoped to the current centre by the
/// global query filter.
/// </summary>
public record GetRadAiUsageQuery(int Days = 30) : IRequest<RadAiUsageResult>;

public record RadAiUsageResult(
    int WindowDays,
    int TotalRequests,
    int CacheHits,
    double CacheHitRatePct,
    // tokens
    long BilledInputTokens,
    long BilledOutputTokens,
    long PromptCacheReadTokens,
    long ResponseCacheSavedInputTokens,
    long ResponseCacheSavedOutputTokens,
    // cost (USD, at the configured Haiku rate)
    decimal BaselineCostUsd,   // what it WOULD cost with no caching
    decimal ActualCostUsd,     // what it actually cost
    decimal SavedCostUsd,      // baseline - actual
    double SavedPct,
    decimal ResponseCacheSavedUsd,
    decimal PromptCacheSavedUsd,
    decimal InputRatePerMTok,
    decimal OutputRatePerMTok,
    string Note);

public class GetRadAiUsageQueryHandler : IRequestHandler<GetRadAiUsageQuery, RadAiUsageResult>
{
    private readonly IApplicationDbContext _db;

    public GetRadAiUsageQueryHandler(IApplicationDbContext db) => _db = db;

    // Set these to your plan's Claude Haiku price (USD per 1M tokens). Prompt-
    // cache reads bill at ~10% of the input rate. These are the ONLY assumptions
    // in the cost numbers — token counts above are real/measured.
    private const decimal InputRatePerMTok = 1.00m;
    private const decimal OutputRatePerMTok = 5.00m;
    private const decimal CacheReadDiscount = 0.10m;

    public async Task<RadAiUsageResult> Handle(GetRadAiUsageQuery q, CancellationToken ct)
    {
        var days = Math.Clamp(q.Days, 1, 365);
        var since = DateTime.UtcNow.AddDays(-days);

        var rows = await _db.RadAiQuestionLogs
            .Where(l => l.CreatedAt >= since)
            .Select(l => new
            {
                l.CacheHit,
                l.InputTokens,
                l.OutputTokens,
                l.CacheReadInputTokens,
                l.SavedInputTokens,
                l.SavedOutputTokens
            })
            .ToListAsync(ct);

        int total = rows.Count;
        int hits = rows.Count(r => r.CacheHit);
        long billedIn = rows.Sum(r => (long)r.InputTokens);            // uncached input actually billed
        long billedOut = rows.Sum(r => (long)r.OutputTokens);
        long cacheRead = rows.Sum(r => (long)r.CacheReadInputTokens);  // system-prompt tokens served from prompt cache
        long savedIn = rows.Sum(r => (long)r.SavedInputTokens);        // input avoided by response-cache hits
        long savedOut = rows.Sum(r => (long)r.SavedOutputTokens);      // output avoided by response-cache hits

        decimal In(long t) => t / 1_000_000m * InputRatePerMTok;
        decimal Out(long t) => t / 1_000_000m * OutputRatePerMTok;

        // What it actually cost: uncached input at full rate, prompt-cache reads
        // at the discount, output at full rate.
        decimal actual = In(billedIn) + In(cacheRead) * CacheReadDiscount + Out(billedOut);
        // Response-cache savings: the model calls we skipped entirely.
        decimal respSaved = In(savedIn) + Out(savedOut);
        // Prompt-cache savings: the 90% we didn't pay on cached input tokens.
        decimal promptSaved = In(cacheRead) * (1 - CacheReadDiscount);
        decimal baseline = actual + respSaved + promptSaved;
        decimal saved = respSaved + promptSaved;
        double savedPct = baseline > 0 ? (double)(saved / baseline) * 100 : 0;
        double hitRate = total > 0 ? (double)hits / total * 100 : 0;

        return new RadAiUsageResult(
            days, total, hits, Math.Round(hitRate, 1),
            billedIn, billedOut, cacheRead, savedIn, savedOut,
            Math.Round(baseline, 4), Math.Round(actual, 4), Math.Round(saved, 4), Math.Round(savedPct, 1),
            Math.Round(respSaved, 4), Math.Round(promptSaved, 4),
            InputRatePerMTok, OutputRatePerMTok,
            "Token counts are measured; cost uses the configured Haiku rate. Prompt-cache savings require the system prompt to recur within Anthropic's cache window.");
    }
}
