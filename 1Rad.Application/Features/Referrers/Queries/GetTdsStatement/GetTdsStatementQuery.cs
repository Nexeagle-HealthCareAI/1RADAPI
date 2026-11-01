using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.GetTdsStatement;

/// <summary>
/// A partner's payouts for one financial year with gross, TDS and net (review R10):
/// what the centre needs for its TDS return and to give the payee a statement.
/// Payouts made before payout records existed are listed from the commission rows
/// (gross only, no TDS recorded).
/// </summary>
public record GetTdsStatementQuery(Guid ReferrerId, string? FinancialYear) : IRequest<TdsStatementDto>;

public record TdsStatementLine(string? PayoutNumber, DateTime PaidAt, string Mode, string? Reference, decimal Gross,
    decimal DeficitRecovered, string TdsCategory, decimal TdsRate, decimal TdsAmount, decimal Net, bool Legacy);
public record TdsStatementDto(Guid ReferrerId, string PayeeName, string? Pan, string FinancialYear,
    List<TdsStatementLine> Lines, decimal Gross, decimal TdsAmount, decimal Net);

public class GetTdsStatementQueryHandler : IRequestHandler<GetTdsStatementQuery, TdsStatementDto>
{
    private readonly IApplicationDbContext _db;
    public GetTdsStatementQueryHandler(IApplicationDbContext db) => _db = db;

    public async Task<TdsStatementDto> Handle(GetTdsStatementQuery q, CancellationToken ct)
    {
        var hospitalId = _db.UserContext.HospitalId;
        var fy = string.IsNullOrWhiteSpace(q.FinancialYear) ? InvoiceNumbering.FinancialYearLabel(DateTime.UtcNow) : q.FinancialYear.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(fy, @"^\d{4}-\d{2}$"))
            throw new ValidationException("Financial year should look like 2026-27.");

        var registry = await _db.Referrers.AsNoTracking().Where(r => r.HospitalId == hospitalId)
            .Select(r => new { r.ReferrerId, r.MergedIntoId, r.Name, r.Pan }).ToListAsync(ct);
        var map = registry.ToDictionary(r => r.ReferrerId);
        Guid Root(Guid id)
        {
            var seen = new HashSet<Guid>();
            while (map.TryGetValue(id, out var r) && r.MergedIntoId.HasValue && seen.Add(id)) id = r.MergedIntoId.Value;
            return id;
        }
        if (!map.ContainsKey(q.ReferrerId)) throw new NotFoundException("Partner not found.");
        var root = Root(q.ReferrerId);
        var aliasIds = registry.Where(r => Root(r.ReferrerId) == root).Select(r => r.ReferrerId).ToList();
        var partner = map[root];

        var lines = await _db.ReferralPayouts.AsNoTracking()
            .Where(p => p.HospitalId == hospitalId && p.ReferrerId == root && p.FinancialYear == fy)
            .OrderBy(p => p.PaidAt)
            .Select(p => new TdsStatementLine(p.PayoutNumber, p.PaidAt, p.Mode, p.Reference, p.Gross, p.DeficitRecovered,
                p.TdsCategory, p.TdsRate, p.TdsAmount, p.Net, false))
            .ToListAsync(ct);

        var (start, end) = Features.Referrers.Commands.PayReferralCommissions.PayReferralCommissionsCommandHandler.FinancialYearBounds(fy);
        var legacy = await _db.ReferralCommissions.AsNoTracking()
            .Where(c => c.HospitalId == hospitalId && aliasIds.Contains(c.ReferrerId) && c.PayoutId == null
                        && c.Status == CommissionStatus.Paid && c.CommissionAmount > 0
                        && c.PaymentDate >= start && c.PaymentDate < end)
            .Select(c => new { c.PaymentDate, c.CommissionAmount })
            .ToListAsync(ct);
        lines.AddRange(legacy.GroupBy(c => c.PaymentDate!.Value.Date).Select(g =>
            new TdsStatementLine(null, g.Key, "—", null, g.Sum(x => x.CommissionAmount), 0, "NONE", 0, 0, g.Sum(x => x.CommissionAmount), true)));
        lines = lines.OrderBy(l => l.PaidAt).ToList();

        return new TdsStatementDto(root, partner.Name, partner.Pan, fy, lines,
            lines.Sum(l => l.Gross - l.DeficitRecovered), lines.Sum(l => l.TdsAmount), lines.Sum(l => l.Net));
    }
}
