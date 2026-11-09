using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.AiPolicy;

/// <summary>The centre's report-AI rules plus this month's use (for the editor and Admin).</summary>
public record GetAiPolicyQuery : IRequest<AiPolicyDto>;

public record AiPolicyDto(
    bool ReportAiEnabled,
    bool AllowCloudDictation,
    int MonthlyCallLimit,
    bool UsesDefaultLimit,
    int UsedThisMonth);

public class GetAiPolicyQueryHandler : IRequestHandler<GetAiPolicyQuery, AiPolicyDto>
{
    private readonly IApplicationDbContext _db;
    public GetAiPolicyQueryHandler(IApplicationDbContext db) => _db = db;

    public async Task<AiPolicyDto> Handle(GetAiPolicyQuery request, CancellationToken ct)
    {
        var p = await _db.CentreAiPolicies.AsNoTracking().FirstOrDefaultAsync(ct);
        var since = ReportAiGate.MonthStartUtc(DateTime.UtcNow);
        var used = await _db.ReportAiUsageLogs.CountAsync(l => l.CreatedAt >= since && l.Outcome != "refused", ct);
        return new AiPolicyDto(
            p?.ReportAiEnabled ?? true,
            p?.AllowCloudDictation ?? false,
            p?.MonthlyCallLimit ?? ReportAiGate.DefaultMonthlyCallLimit,
            p?.MonthlyCallLimit == null,
            used);
    }
}

/// <summary>Admin: change the centre's report-AI rules.</summary>
public record SetAiPolicyCommand(bool ReportAiEnabled, bool AllowCloudDictation, int? MonthlyCallLimit) : IRequest<AiPolicyDto>;

public class SetAiPolicyCommandHandler : IRequestHandler<SetAiPolicyCommand, AiPolicyDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IMediator? _mediator;
    public SetAiPolicyCommandHandler(IApplicationDbContext db, IMediator? mediator = null) { _db = db; _mediator = mediator; }

    public async Task<AiPolicyDto> Handle(SetAiPolicyCommand request, CancellationToken ct)
    {
        if (request.MonthlyCallLimit is int n && (n < 0 || n > 1_000_000))
            throw new ArgumentException("The monthly limit must be between 0 and 1,000,000.");
        var uc = _db.UserContext;
        if (uc.HospitalId == Guid.Empty) throw new UnauthorizedAccessException("No centre selected.");

        var p = await _db.CentreAiPolicies.FirstOrDefaultAsync(ct);
        if (p == null)
        {
            p = new CentreAiPolicy { HospitalId = uc.HospitalId };
            _db.CentreAiPolicies.Add(p);
        }
        p.ReportAiEnabled = request.ReportAiEnabled;
        p.AllowCloudDictation = request.AllowCloudDictation;
        p.MonthlyCallLimit = request.MonthlyCallLimit;
        p.UpdatedAt = DateTime.UtcNow;
        p.UpdatedByUserId = uc.UserId == Guid.Empty ? null : uc.UserId;
        await _db.SaveChangesAsync(ct);

        return await new GetAiPolicyQueryHandler(_db).Handle(new GetAiPolicyQuery(), ct);
    }
}

/// <summary>Admin: what report AI was used for, by feature, with estimated cost.</summary>
public record GetReportAiUsageQuery(int Days = 30) : IRequest<ReportAiUsageDto>;

public record ReportAiUsageRow(string Feature, int Requests, int Errors, int Refused, long InputTokens, long OutputTokens, long CacheReadTokens, decimal CostUsd, int MaskedIdentifiers);
public record ReportAiUsageDto(int Days, IReadOnlyList<ReportAiUsageRow> Features, int Requests, decimal CostUsd, decimal InputRatePerMTok, decimal OutputRatePerMTok, string Note);

public class GetReportAiUsageQueryHandler : IRequestHandler<GetReportAiUsageQuery, ReportAiUsageDto>
{
    // Claude Haiku list price (USD per 1M tokens) — the only assumption in the cost figure.
    public const decimal InputRatePerMTok = 1.00m;
    public const decimal OutputRatePerMTok = 5.00m;
    private const decimal CacheReadDiscount = 0.10m;
    private const decimal CacheWritePremium = 1.25m;

    private readonly IApplicationDbContext _db;
    public GetReportAiUsageQueryHandler(IApplicationDbContext db) => _db = db;

    public static decimal Cost(long input, long output, long cacheRead, long cacheWrite) =>
        (input * InputRatePerMTok + output * OutputRatePerMTok
         + cacheRead * InputRatePerMTok * CacheReadDiscount + cacheWrite * InputRatePerMTok * CacheWritePremium) / 1_000_000m;

    public async Task<ReportAiUsageDto> Handle(GetReportAiUsageQuery q, CancellationToken ct)
    {
        var days = Math.Clamp(q.Days, 1, 365);
        var since = DateTime.UtcNow.AddDays(-days);
        var rows = await _db.ReportAiUsageLogs.AsNoTracking()
            .Where(l => l.CreatedAt >= since)
            .Select(l => new { l.Feature, l.Outcome, l.InputTokens, l.OutputTokens, l.CacheReadInputTokens, l.CacheCreationInputTokens, l.MaskedCount })
            .ToListAsync(ct);

        static string Group(string f) => f.StartsWith("assist:") ? f : f;
        var features = rows
            .GroupBy(r => Group(r.Feature))
            .Select(g => new ReportAiUsageRow(
                g.Key,
                g.Count(r => r.Outcome != "refused"),
                g.Count(r => r.Outcome == "error"),
                g.Count(r => r.Outcome == "refused"),
                g.Sum(r => (long)r.InputTokens),
                g.Sum(r => (long)r.OutputTokens),
                g.Sum(r => (long)r.CacheReadInputTokens),
                Math.Round(Cost(g.Sum(r => (long)r.InputTokens), g.Sum(r => (long)r.OutputTokens), g.Sum(r => (long)r.CacheReadInputTokens), g.Sum(r => (long)r.CacheCreationInputTokens)), 4),
                g.Sum(r => r.MaskedCount)))
            .OrderByDescending(r => r.Requests)
            .ToList();

        return new ReportAiUsageDto(days, features, features.Sum(f => f.Requests), features.Sum(f => f.CostUsd),
            InputRatePerMTok, OutputRatePerMTok,
            "Token counts are the AI provider's own figures. Cost is an estimate at Claude Haiku list prices.");
    }
}
