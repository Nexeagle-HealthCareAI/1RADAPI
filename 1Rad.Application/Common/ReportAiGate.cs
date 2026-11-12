using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Common;

/// <summary>
/// Report-AI rules per centre (see <see cref="CentreAiPolicy"/>):
///   • AI can be switched off for a centre;
///   • a monthly allowance of requests per centre (default 6,000 ≈ 200/day);
///   • a burst limit per user (30 requests in 5 minutes) so one stuck key or
///     script can't burn the month;
///   • every request is logged with real token counts (no report text).
/// </summary>
public class ReportAiGate : IReportAiGate
{
    public const int DefaultMonthlyCallLimit = 6000;
    private const int BurstLimit = 30;
    private static readonly TimeSpan BurstWindow = TimeSpan.FromMinutes(5);

    private readonly IApplicationDbContext _db;
    private readonly IDistributedRateLimiter? _limiter;
    private readonly ILogger<ReportAiGate>? _logger;

    public ReportAiGate(IApplicationDbContext db, IDistributedRateLimiter? limiter = null, ILogger<ReportAiGate>? logger = null)
    {
        _db = db;
        _limiter = limiter;
        _logger = logger;
    }

    public static DateTime MonthStartUtc(DateTime nowUtc) => new(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    public async Task<string?> CheckAsync(string feature, CancellationToken ct)
    {
        var policy = await _db.CentreAiPolicies.AsNoTracking().FirstOrDefaultAsync(ct);
        if (policy is { ReportAiEnabled: false })
            return "AI writing help is switched off for this centre (Admin ▸ AI & privacy).";

        var limit = policy?.MonthlyCallLimit ?? DefaultMonthlyCallLimit;
        var since = MonthStartUtc(DateTime.UtcNow);
        var used = await _db.ReportAiUsageLogs.CountAsync(l => l.CreatedAt >= since && l.Outcome != "refused", ct);
        if (used >= limit)
            return $"This centre has used its {limit:N0} AI requests for this month. An admin can raise the limit in Admin ▸ AI & privacy.";

        var userId = _db.UserContext.UserId;
        if (_limiter != null && userId != Guid.Empty)
        {
            var (allowed, _) = await _limiter.TryAcquireAsync($"report-ai:{userId}", BurstLimit, BurstWindow, ct);
            if (!allowed) return "Too many AI requests in a few minutes — please wait a moment and try again.";
        }
        return null;
    }

    public async Task RecordAsync(string feature, string? model, AiUsage? usage, int maskedCount, string outcome, CancellationToken ct)
    {
        try
        {
            var uc = _db.UserContext;
            _db.ReportAiUsageLogs.Add(new ReportAiUsageLog
            {
                HospitalId = uc.HospitalId,
                UserId = uc.UserId == Guid.Empty ? null : uc.UserId,
                Feature = feature.Length > 40 ? feature[..40] : feature,
                Model = model,
                InputTokens = usage?.InputTokens ?? 0,
                OutputTokens = usage?.OutputTokens ?? 0,
                CacheReadInputTokens = usage?.CacheReadInputTokens ?? 0,
                CacheCreationInputTokens = usage?.CacheCreationInputTokens ?? 0,
                MaskedCount = maskedCount,
                Outcome = outcome,
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Report-AI usage row could not be saved.");
        }
    }
}
