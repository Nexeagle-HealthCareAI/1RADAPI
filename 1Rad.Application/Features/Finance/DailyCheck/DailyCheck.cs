using _1Rad.Application.Common;
using _1Rad.Application.Features.Finance.DayBook;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.DailyCheck;

/// <summary>
/// Money ▸ Daily check (Nov 2026): does the day add up? Visits, bills, money and the
/// cash book for one India date, and every record that does not match, with what is
/// wrong and where to fix it. Read-only: it never changes anything.
///
/// Checks (each one is cheap, and runs over that day's rows only):
///   • NO_BILL         a visit that went ahead (not cancelled, arrived or done) with no bill
///   • CANCELLED_VISIT a live bill on a visit that was cancelled
///   • PAID_MISMATCH   a bill whose "paid" figure is not the sum of its payment rows
///   • TOTAL_MISMATCH  a bill whose total is not price − discount
///   • SHARE_ON_CANCELLED a partner share still owed on a cancelled bill or visit
///   • DUPLICATE       the same patient with the same test twice that day
///   • NOT_CLOSED      the cash drawer was not closed (only for days before today)
///   • CASH_DIFFERENCE the drawer count did not match what the cash book expected
/// </summary>
public record GetDailyCheckQuery(DateTime Date) : IRequest<DailyCheckDto>;

public record DailyCheckIssue(
    string Kind, string Severity, string Title, string? Detail,
    Guid? InvoiceId, string? BillNumber, Guid? AppointmentId, string? PatientName, decimal? Amount);

public record DailyCheckCounts(int Visits, int VisitsCancelled, int Bills, int BillsCancelled, decimal Billed, decimal MoneyIn, decimal Cash, bool Closed);

public record DailyCheckDto(DateTime Date, DailyCheckCounts Counts, List<DailyCheckIssue> Issues, List<string> Passed);

public class GetDailyCheckQueryHandler : IRequestHandler<GetDailyCheckQuery, DailyCheckDto>
{
    private const decimal Paisa = 0.5m; // differences under 50 paise are rounding, not errors
    private readonly IApplicationDbContext _db;
    public GetDailyCheckQueryHandler(IApplicationDbContext db) => _db = db;

    public async Task<DailyCheckDto> Handle(GetDailyCheckQuery q, CancellationToken ct)
    {
        var h = _db.UserContext.HospitalId;
        var day = q.Date.Date;
        var today = IstDateRange.ToIst(DateTime.UtcNow).Date;
        var s = IstDateRange.ToUtcStart(day);
        var e = IstDateRange.ToUtcEndInclusive(day);
        var cs = IstDateRange.ClockStart(day);
        var ce = IstDateRange.ClockEndInclusive(day);
        var issues = new List<DailyCheckIssue>();

        // ── The day's visits and bills (small: a busy centre has a few hundred).
        var visits = await _db.Appointments.AsNoTracking()
            .Where(a => a.HospitalId == h && a.DeletedAt == null && a.DateTime >= cs && a.DateTime <= ce)
            .Select(a => new { a.AppointmentId, a.PatientId, a.PatientName, a.Status, a.ArrivedAt, a.DisplayId })
            .ToListAsync(ct);
        var visitIds = visits.Select(v => v.AppointmentId).ToList();

        var bills = await _db.Invoices.AsNoTracking()
            .Where(i => i.HospitalId == h && i.DeletedAt == null
                        && ((i.CreatedAt >= s && i.CreatedAt <= e) || (i.AppointmentId != null && visitIds.Contains(i.AppointmentId.Value))))
            .Select(i => new { i.Id, i.InvoiceId, i.AppointmentId, i.PatientName, i.Status, i.GrossAmount, i.DiscountAmount, i.TotalAmount, i.PaidAmount, i.CreatedAt })
            .ToListAsync(ct);
        var billIds = bills.Select(b => b.Id).ToList();
        var paidRows = await _db.Payments.AsNoTracking()
            .Where(p => billIds.Contains(p.InvoiceId))
            .GroupBy(p => p.InvoiceId)
            .Select(g => new { g.Key, Sum = g.Sum(p => p.Amount), N = g.Count() })
            .ToListAsync(ct);

        static bool Cancelled(string? st) => string.Equals(st, "CANCELLED", StringComparison.OrdinalIgnoreCase);
        var billByVisit = bills.Where(b => b.AppointmentId != null).GroupBy(b => b.AppointmentId!.Value).ToDictionary(g => g.Key, g => g.ToList());

        // NO_BILL: a visit that went ahead but has no live bill.
        foreach (var v in visits.Where(v => !Cancelled(v.Status) && (v.ArrivedAt != null || IsDone(v.Status))))
        {
            var hasBill = billByVisit.TryGetValue(v.AppointmentId, out var bs) && bs.Any(b => !Cancelled(b.Status));
            if (!hasBill)
                issues.Add(new DailyCheckIssue("NO_BILL", "high", "Visit with no bill", v.DisplayId, null, null, v.AppointmentId, v.PatientName, null));
        }

        // CANCELLED_VISIT: a live bill on a cancelled visit.
        foreach (var v in visits.Where(v => Cancelled(v.Status)))
            if (billByVisit.TryGetValue(v.AppointmentId, out var bs))
                foreach (var b in bs.Where(b => !Cancelled(b.Status)))
                    issues.Add(new DailyCheckIssue("CANCELLED_VISIT", "high", "Bill still open on a cancelled visit", v.DisplayId, b.Id, b.InvoiceId, v.AppointmentId, b.PatientName, b.TotalAmount));

        foreach (var b in bills)
        {
            // PAID_MISMATCH: only bills that have payment rows (older imports have none).
            var rows = paidRows.FirstOrDefault(r => r.Key == b.Id);
            if (rows != null && Math.Abs(rows.Sum - b.PaidAmount) > Paisa)
                issues.Add(new DailyCheckIssue("PAID_MISMATCH", "high", "Paid figure does not match the payments",
                    $"Bill says {b.PaidAmount:0.##}, payments add up to {rows.Sum:0.##}", b.Id, b.InvoiceId, b.AppointmentId, b.PatientName, rows.Sum - b.PaidAmount));
            // TOTAL_MISMATCH
            if (!Cancelled(b.Status) && Math.Abs(b.GrossAmount - b.DiscountAmount - b.TotalAmount) > Paisa)
                issues.Add(new DailyCheckIssue("TOTAL_MISMATCH", "high", "Total is not price minus discount",
                    $"{b.GrossAmount:0.##} − {b.DiscountAmount:0.##} ≠ {b.TotalAmount:0.##}", b.Id, b.InvoiceId, b.AppointmentId, b.PatientName, b.TotalAmount));
        }

        // SHARE_ON_CANCELLED: an unpaid partner share on a cancelled bill or visit.
        var cancelledVisitIds = visits.Where(v => Cancelled(v.Status)).Select(v => v.AppointmentId)
            .Concat(bills.Where(b => Cancelled(b.Status) && b.AppointmentId != null
                                     && !(billByVisit.TryGetValue(b.AppointmentId!.Value, out var all) && all.Any(x => !Cancelled(x.Status))))
                         .Select(b => b.AppointmentId!.Value))
            .Distinct().ToList();
        if (cancelledVisitIds.Count > 0)
        {
            var shares = await _db.ReferralCommissions.AsNoTracking()
                .Where(c => c.HospitalId == h && c.DeletedAt == null && c.AppointmentId != null && cancelledVisitIds.Contains(c.AppointmentId.Value)
                            && c.Status != CommissionStatus.Cancelled && c.Status != "CANCELLED" && c.CommissionAmount > 0)
                .Select(c => new { c.AppointmentId, c.ReferrerName, c.CommissionAmount, c.PatientName, c.Status })
                .ToListAsync(ct);
            foreach (var c in shares)
                issues.Add(new DailyCheckIssue("SHARE_ON_CANCELLED", "high",
                    CommissionStatus.IsPaid(c.Status) ? "Partner was paid for a cancelled visit" : "Partner share still owed on a cancelled visit",
                    c.ReferrerName, null, null, c.AppointmentId, c.PatientName, c.CommissionAmount));
        }

        // DUPLICATE: same patient, same test, twice in the day (live bills).
        var lines = await _db.Invoices.AsNoTracking()
            .Where(i => billIds.Contains(i.Id) && i.Status != "CANCELLED")
            .SelectMany(i => i.Items.Select(it => new { i.Id, i.InvoiceId, i.PatientId, i.PatientName, it.Description }))
            .ToListAsync(ct);
        foreach (var d in lines.GroupBy(l => new { l.PatientId, Test = (l.Description ?? string.Empty).Trim().ToUpperInvariant() })
                               .Where(g => g.Key.Test.Length > 0 && g.Count() > 1))
        {
            var first = d.First();
            issues.Add(new DailyCheckIssue("DUPLICATE", "medium", "Same test twice for one patient",
                $"{first.Description} · {string.Join(", ", d.Select(x => x.InvoiceId).Distinct())}", first.Id, first.InvoiceId, null, first.PatientName, null));
        }

        // ── Money in and the cash book.
        var pays = await _db.Payments.AsNoTracking()
            .Where(p => p.HospitalId == h && p.CreatedAt >= s && p.CreatedAt <= e)
            .GroupBy(p => p.PaymentMethod).Select(g => new { Mode = g.Key, Net = g.Sum(p => p.Amount) }).ToListAsync(ct);
        var byMode = pays.Select(p => new { Mode = DayBookModes.Normalise(p.Mode), p.Net }).Where(p => p.Mode != null).ToList();
        var moneyIn = byMode.Sum(p => p.Net);
        var cash = byMode.Where(p => p.Mode == "CASH").Sum(p => p.Net);
        var close = await _db.DayCloses.AsNoTracking().Where(d => d.HospitalId == h && d.BusinessDate == day)
            .Select(d => new { d.Variance, d.DifferenceReason, d.ClosedByName }).FirstOrDefaultAsync(ct);
        if (close == null && day < today && Math.Abs(cash) > Paisa)
            issues.Add(new DailyCheckIssue("NOT_CLOSED", "medium", "Cash drawer not closed", null, null, null, null, null, cash));
        if (close != null && Math.Abs(close.Variance) > Paisa)
            issues.Add(new DailyCheckIssue("CASH_DIFFERENCE", close.DifferenceReason == null ? "high" : "low",
                close.Variance > 0 ? "More cash in the drawer than expected" : "Less cash in the drawer than expected",
                close.DifferenceReason ?? close.ClosedByName, null, null, null, null, close.Variance));

        var order = new[] { "high", "medium", "low" };
        var passed = new[] { "NO_BILL", "CANCELLED_VISIT", "PAID_MISMATCH", "TOTAL_MISMATCH", "SHARE_ON_CANCELLED", "DUPLICATE", "NOT_CLOSED", "CASH_DIFFERENCE" }
            .Where(k => issues.All(i => i.Kind != k)).ToList();
        var live = bills.Where(b => !Cancelled(b.Status) && b.CreatedAt >= s && b.CreatedAt <= e).ToList();
        return new DailyCheckDto(day,
            new DailyCheckCounts(visits.Count(v => !Cancelled(v.Status)), visits.Count(v => Cancelled(v.Status)),
                live.Count, bills.Count(b => Cancelled(b.Status) && b.CreatedAt >= s && b.CreatedAt <= e),
                live.Sum(b => b.TotalAmount), Math.Round(moneyIn, 2), Math.Round(cash, 2), close != null),
            issues.OrderBy(i => Array.IndexOf(order, i.Severity)).ThenBy(i => i.Kind).ToList(),
            passed);
    }

    private static bool IsDone(string? status)
    {
        var st = (status ?? string.Empty).ToUpperInvariant();
        return st is "COMPLETED" or "DELIVERED" or "IN_PROGRESS" or "SCANNED" or "REPORTED" or "CONFIRMED" or "ARRIVED";
    }
}
