using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Overview;

/// <summary>
/// Money ▸ Overview (owner, accountant). Four numbers that always add up, for the
/// bills made in the period (India dates, cancelled bills left out):
///   Billed = Collected + Still to collect,   Centre keeps = Billed − Partner share.
/// Next to them, the cash view (money actually received in the period, by mode),
/// the tests that bring the money, a day-by-day series and what needs the owner.
/// Everything is summed in SQL; nothing loads whole tables.
/// </summary>
public record GetMoneyOverviewQuery(DateTime From, DateTime To) : IRequest<MoneyOverviewDto>;

public record OverviewTest(string Test, int Count, decimal Billed);
public record OverviewDay(DateTime Date, decimal Billed, decimal Received);
public record OverviewMode(string Mode, decimal Net);
public record OverviewNeeds(int Approvals, int OldDueBills, decimal OldDueAmount, int DaysNotClosed, bool TdsNotSet);

public record MoneyOverviewDto(
    DateTime From, DateTime To,
    int Bills, int Cancelled,
    decimal Gross, decimal Discounts, decimal Billed, decimal Collected, decimal Due,
    decimal PartnerShare, decimal CentreKeeps,
    decimal Received, List<OverviewMode> ReceivedByMode, decimal ExpensesPaid,
    List<OverviewTest> Tests, List<OverviewDay> Days, OverviewNeeds Needs);

public class GetMoneyOverviewQueryHandler : IRequestHandler<GetMoneyOverviewQuery, MoneyOverviewDto>
{
    private readonly IApplicationDbContext _db;
    public GetMoneyOverviewQueryHandler(IApplicationDbContext db) => _db = db;

    public async Task<MoneyOverviewDto> Handle(GetMoneyOverviewQuery q, CancellationToken ct)
    {
        var hospitalId = _db.UserContext.HospitalId;
        var from = q.From.Date;
        var to = q.To.Date < from ? from : q.To.Date;
        if ((to - from).TotalDays > 366) throw new ValidationException("The overview covers at most one year at a time.");
        var start = IstDateRange.ToUtcStart(from);
        var end = IstDateRange.ToUtcEndInclusive(to);

        var bills = _db.Invoices.AsNoTracking()
            .Where(i => i.HospitalId == hospitalId && i.DeletedAt == null && i.CreatedAt >= start && i.CreatedAt <= end);
        var live = bills.Where(i => i.Status != "CANCELLED");

        var sums = await live.GroupBy(_ => 1).Select(g => new
        {
            N = g.Count(),
            Gross = g.Sum(i => i.GrossAmount),
            Billed = g.Sum(i => i.TotalAmount),
            Collected = g.Sum(i => i.PaidAmount > i.TotalAmount ? i.TotalAmount : i.PaidAmount),
        }).FirstOrDefaultAsync(ct);
        var cancelled = await bills.CountAsync(i => i.Status == "CANCELLED", ct);
        var billed = sums?.Billed ?? 0m;
        var collected = sums?.Collected ?? 0m;
        var gross = sums?.Gross ?? 0m;

        // Partner share earned on these bills (not cancelled, not deleted).
        var share = await _db.ReferralCommissions.AsNoTracking()
            .Where(c => c.HospitalId == hospitalId && c.DeletedAt == null
                        && c.Status != CommissionStatus.Cancelled && c.Status != "CANCELLED"
                        && c.AppointmentId != null
                        && live.Any(i => i.AppointmentId == c.AppointmentId))
            .SumAsync(c => (decimal?)c.CommissionAmount, ct) ?? 0m;

        // Cash view: money that actually came in (or went back) in the period.
        var moneyRows = await _db.Payments.AsNoTracking()
            .Where(p => p.HospitalId == hospitalId && p.CreatedAt >= start && p.CreatedAt <= end)
            .GroupBy(p => p.PaymentMethod)
            .Select(g => new { Mode = g.Key, Net = g.Sum(p => p.Amount) })
            .ToListAsync(ct);
        var byMode = moneyRows
            .Select(r => new { Mode = _1Rad.Application.Features.Finance.DayBook.DayBookModes.Normalise(r.Mode), r.Net })
            .Where(r => r.Mode != null)
            .GroupBy(r => r.Mode!)
            .Select(g => new OverviewMode(g.Key, g.Sum(r => r.Net)))
            .OrderBy(m => Array.IndexOf(new[] { "CASH", "UPI", "CARD", "BANK", "OTHER" }, m.Mode))
            .ToList();

        var expensesPaid = await _db.Expenses.AsNoTracking()
            .Where(e => e.HospitalId == hospitalId && e.DeletedAt == null && e.Status == "Paid"
                        && (e.PaidAt ?? e.TransactionDate) >= start && (e.PaidAt ?? e.TransactionDate) <= end)
            .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;

        var tests = (await live.SelectMany(i => i.Items)
                .GroupBy(it => it.Description)
                .Select(g => new { Test = g.Key, N = g.Count(), Billed = g.Sum(it => it.IsFree ? 0m : it.Amount * it.Quantity) })
                .OrderByDescending(t => t.Billed)
                .Take(8)
                .ToListAsync(ct))
            .Select(t => new OverviewTest(t.Test, t.N, t.Billed)).ToList();

        // Day by day (at most 62 days, so a month view gets bars; a year view does not).
        var days = new List<OverviewDay>();
        if ((to - from).TotalDays <= 62)
        {
            var billedRows = await live.Select(i => new { i.CreatedAt, i.TotalAmount }).ToListAsync(ct);
            var paidRows = await _db.Payments.AsNoTracking()
                .Where(p => p.HospitalId == hospitalId && p.CreatedAt >= start && p.CreatedAt <= end && p.PaymentMethod != "ADVANCE")
                .Select(p => new { p.CreatedAt, p.Amount }).ToListAsync(ct);
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                var day = d;
                days.Add(new OverviewDay(day,
                    billedRows.Where(r => IstDateRange.ToIst(r.CreatedAt).Date == day).Sum(r => r.TotalAmount),
                    paidRows.Where(r => IstDateRange.ToIst(r.CreatedAt).Date == day).Sum(r => r.Amount)));
            }
        }

        // What needs the owner.
        var approvals = await _db.ApprovalRequests.AsNoTracking()
            .CountAsync(a => a.HospitalId == hospitalId && a.DeletedAt == null && a.Status == "PENDING", ct);
        var monthAgo = DateTime.UtcNow.AddDays(-30);
        var old = await _db.Invoices.AsNoTracking()
            .Where(i => i.HospitalId == hospitalId && i.DeletedAt == null && i.CreatedAt < monthAgo
                        && (i.Status == "PENDING" || i.Status == "PARTIAL") && i.TotalAmount - i.PaidAmount > 0.009m)
            .GroupBy(_ => 1).Select(g => new { N = g.Count(), Amt = g.Sum(i => i.TotalAmount - i.PaidAmount) })
            .FirstOrDefaultAsync(ct);
        var today = IstDateRange.ToIst(DateTime.UtcNow).Date;
        var weekStart = today.AddDays(-7);
        var closed = await _db.DayCloses.AsNoTracking()
            .Where(d => d.HospitalId == hospitalId && d.BusinessDate >= weekStart && d.BusinessDate < today)
            .Select(d => d.BusinessDate).ToListAsync(ct);
        var cashDays = await _db.Payments.AsNoTracking()
            .Where(p => p.HospitalId == hospitalId && p.PaymentMethod == "CASH"
                        && p.CreatedAt >= IstDateRange.ToUtcStart(weekStart) && p.CreatedAt < IstDateRange.ToUtcStart(today))
            .Select(p => p.CreatedAt).ToListAsync(ct);
        var openDays = cashDays.Select(c => IstDateRange.ToIst(c).Date).Distinct().Count(d => !closed.Contains(d));
        var tdsNotSet = await _db.ReferralCommissions.AsNoTracking().AnyAsync(c => c.HospitalId == hospitalId && c.DeletedAt == null, ct)
                        && !await _db.HospitalBillingSettings.AsNoTracking().AnyAsync(s => s.HospitalId == hospitalId && (!s.TdsEnabled || s.TdsCommissionRate != null), ct);

        return new MoneyOverviewDto(from, to, sums?.N ?? 0, cancelled,
            gross, Math.Round(gross - billed, 2), billed, collected, Math.Round(billed - collected, 2),
            share, Math.Round(billed - share, 2),
            byMode.Sum(m => m.Net), byMode, expensesPaid,
            tests, days,
            new OverviewNeeds(approvals, old?.N ?? 0, old?.Amt ?? 0m, openDays, tdsNotSet));
    }
}
