using _1Rad.Application.Common;
using _1Rad.Application.Features.Appointments.Queries.GetOverdueAppointments;
using _1Rad.Application.Features.Finance.DayBook;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Overview;

/// <summary>
/// Money ▸ Overview, rebuilt as "the centre at a glance" (Nov 2026). One money
/// idea throughout — <b>money in</b>: what was actually received (cash, UPI, card,
/// bank), less money given back, by the day it came in. The same figure the Cash
/// book shows, so the two screens always agree.
///   • Today: patients, money in by mode, cash that should be in the drawer, what
///     today's bills still owe — each next to yesterday and the same day last week.
///   • The month so far: money in − expenses paid − partners' share = what the
///     centre earned, next to the same days of the month before.
///   • By modality and by referrer, for the month: patients and money.
///   • Needs you: everything waiting, with a count.
/// Patients are visits (appointments), not bill lines. Money per modality is each
/// bill's final amount split over its lines, so the modalities add up to the bills.
/// </summary>
public record GetCentreGlanceQuery(DateTime? Month = null, int LateAfterMinutes = 180) : IRequest<CentreGlanceDto>;

public record GlanceMode(string Mode, decimal Amount);
public record GlanceDayCompare(int Patients, decimal MoneyIn);
public record GlanceToday(
    DateTime Date, int Patients, int ReportsGiven, int Bills, decimal MoneyIn, List<GlanceMode> ByMode,
    decimal CashInDrawer, decimal OpeningCash, decimal ToCollect, bool Closed,
    GlanceDayCompare Yesterday, GlanceDayCompare LastWeek);
public record GlanceMoney(int Patients, int Bills, decimal MoneyIn, decimal Expenses, decimal PartnerShare, decimal Earned, decimal Billed, decimal Discounts);
public record GlanceDay(DateTime Date, decimal MoneyIn, int Patients);
public record GlanceMonth(DateTime From, DateTime To, GlanceMoney Now, DateTime PrevFrom, DateTime PrevTo, GlanceMoney Before, List<GlanceDay> Days);
public record GlanceModality(string Modality, int Patients, decimal Money, int PrevPatients, decimal PrevMoney);
public record GlanceReferrer(Guid? ReferrerId, string Name, int Patients, decimal Billed, decimal Share);
public record GlanceNeeds(
    int Approvals, int UnpaidBills, decimal UnpaidAmount, int OldDueBills, decimal OldDueAmount,
    int DaysNotClosed, int LateReports, decimal PartnersUnpaid, int ExpensesToPay, decimal ExpensesToPayAmount, bool TdsNotSet);
public record CentreGlanceDto(GlanceToday Today, GlanceMonth Month, List<GlanceModality> Modalities, List<GlanceReferrer> Referrers, GlanceNeeds Needs);

public class GetCentreGlanceQueryHandler : IRequestHandler<GetCentreGlanceQuery, CentreGlanceDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IMediator? _mediator;
    public GetCentreGlanceQueryHandler(IApplicationDbContext db, IMediator? mediator = null)
    {
        _db = db;
        _mediator = mediator;
    }

    private Guid H => _db.UserContext.HospitalId;

    public async Task<CentreGlanceDto> Handle(GetCentreGlanceQuery q, CancellationToken ct)
    {
        var today = IstDateRange.ToIst(DateTime.UtcNow).Date;

        // ── The month: this month to today, or a whole past month. Compared with the
        //    same days of the month before (1st … same day), so a half month is never
        //    set against a whole one.
        var m = (q.Month ?? today).Date;
        var from = new DateTime(m.Year, m.Month, 1);
        if (from > today) from = new DateTime(today.Year, today.Month, 1);
        var monthEnd = from.AddMonths(1).AddDays(-1);
        var to = monthEnd < today ? monthEnd : today;
        var prevFrom = from.AddMonths(-1);
        var prevEnd = from.AddDays(-1);
        var prevTo = to == monthEnd ? prevEnd : prevFrom.AddDays(Math.Min(to.Day, prevEnd.Day) - 1);

        // ── Today
        var todayIn = await MoneyInByModeAsync(today, today, ct);
        var yIn = await MoneyInAsync(today.AddDays(-1), today.AddDays(-1), ct);
        var wIn = await MoneyInAsync(today.AddDays(-7), today.AddDays(-7), ct);
        var opening = await GetDayBookQueryHandler.OpeningFloatAsync(_db, H, today, ct);
        var cashToday = todayIn.Where(x => x.Mode == "CASH").Sum(x => x.Amount);
        var todayBills = LiveBills(today, today);
        var toCollectToday = await todayBills.SumAsync(i => (decimal?)(i.TotalAmount - i.PaidAmount > 0 ? i.TotalAmount - i.PaidAmount : 0), ct) ?? 0m;
        var tStart = IstDateRange.ToUtcStart(today);
        var tEnd = IstDateRange.ToUtcEndInclusive(today);
        var reportsGiven = await _db.Appointments.AsNoTracking()
            .CountAsync(a => a.HospitalId == H && a.DeletedAt == null && a.DeliveredAt >= tStart && a.DeliveredAt <= tEnd, ct);
        var closed = await _db.DayCloses.AsNoTracking().AnyAsync(d => d.HospitalId == H && d.BusinessDate == today, ct);
        var todayDto = new GlanceToday(today,
            await PatientsAsync(today, today, ct), reportsGiven, await todayBills.CountAsync(ct),
            todayIn.Sum(x => x.Amount), todayIn, opening + cashToday, opening, Math.Round(toCollectToday, 2), closed,
            new GlanceDayCompare(await PatientsAsync(today.AddDays(-1), today.AddDays(-1), ct), yIn),
            new GlanceDayCompare(await PatientsAsync(today.AddDays(-7), today.AddDays(-7), ct), wIn));

        // ── Month and the month before
        var now = await MoneyAsync(from, to, ct);
        var before = await MoneyAsync(prevFrom, prevTo, ct);
        var days = await DaysAsync(from, to, ct);

        // ── By modality, by referrer
        var modNow = await ModalitiesAsync(from, to, ct);
        var modPrev = await ModalitiesAsync(prevFrom, prevTo, ct);
        var modalities = modNow.Keys.Union(modPrev.Keys)
            .Select(k => new GlanceModality(k,
                modNow.TryGetValue(k, out var a) ? a.Patients : 0, modNow.TryGetValue(k, out var a2) ? a2.Money : 0m,
                modPrev.TryGetValue(k, out var b) ? b.Patients : 0, modPrev.TryGetValue(k, out var b2) ? b2.Money : 0m))
            .Where(x => x.Patients > 0 || x.Money != 0 || x.PrevMoney != 0)
            .OrderByDescending(x => x.Money).ThenByDescending(x => x.PrevMoney)
            .ToList();
        var referrers = await ReferrersAsync(from, to, ct);

        return new CentreGlanceDto(todayDto,
            new GlanceMonth(from, to, now, prevFrom, prevTo, before, days),
            modalities, referrers, await NeedsAsync(today, q.LateAfterMinutes, ct));
    }

    // Bills made in an India date range, not cancelled or deleted.
    private IQueryable<Invoice> LiveBills(DateTime from, DateTime to)
    {
        var s = IstDateRange.ToUtcStart(from);
        var e = IstDateRange.ToUtcEndInclusive(to);
        return _db.Invoices.AsNoTracking()
            .Where(i => i.HospitalId == H && i.DeletedAt == null && i.Status != "CANCELLED" && i.CreatedAt >= s && i.CreatedAt <= e);
    }

    // Visits on those India dates (Appointment.DateTime is India clock time), not cancelled.
    private Task<int> PatientsAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var s = IstDateRange.ClockStart(from);
        var e = IstDateRange.ClockEndInclusive(to);
        return _db.Appointments.AsNoTracking()
            .CountAsync(a => a.HospitalId == H && a.DeletedAt == null && a.Status != "CANCELLED" && a.DateTime >= s && a.DateTime <= e, ct);
    }

    // Money in, by mode, the way the Cash book counts it (wallet moves are not money in).
    private async Task<List<GlanceMode>> MoneyInByModeAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var s = IstDateRange.ToUtcStart(from);
        var e = IstDateRange.ToUtcEndInclusive(to);
        var rows = await _db.Payments.AsNoTracking()
            .Where(p => p.HospitalId == H && p.CreatedAt >= s && p.CreatedAt <= e)
            .GroupBy(p => p.PaymentMethod)
            .Select(g => new { Mode = g.Key, Net = g.Sum(p => p.Amount) })
            .ToListAsync(ct);
        var order = new[] { "CASH", "UPI", "CARD", "BANK", "OTHER" };
        return rows.Select(r => new { Mode = DayBookModes.Normalise(r.Mode), r.Net })
            .Where(r => r.Mode != null)
            .GroupBy(r => r.Mode!)
            .Select(g => new GlanceMode(g.Key, Math.Round(g.Sum(r => r.Net), 2)))
            .OrderBy(x => Array.IndexOf(order, x.Mode))
            .ToList();
    }

    private async Task<decimal> MoneyInAsync(DateTime from, DateTime to, CancellationToken ct) =>
        (await MoneyInByModeAsync(from, to, ct)).Sum(x => x.Amount);

    private async Task<GlanceMoney> MoneyAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var s = IstDateRange.ToUtcStart(from);
        var e = IstDateRange.ToUtcEndInclusive(to);
        var bills = LiveBills(from, to);
        var sums = await bills.GroupBy(_ => 1)
            .Select(g => new { N = g.Count(), Billed = g.Sum(i => i.TotalAmount), Gross = g.Sum(i => i.GrossAmount) })
            .FirstOrDefaultAsync(ct);
        var moneyIn = await MoneyInAsync(from, to, ct);
        var expenses = await _db.Expenses.AsNoTracking()
            .Where(x => x.HospitalId == H && x.DeletedAt == null && x.Status == "Paid"
                        && (x.PaidAt ?? x.TransactionDate) >= s && (x.PaidAt ?? x.TransactionDate) <= e)
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
        var share = await _db.ReferralCommissions.AsNoTracking()
            .Where(c => c.HospitalId == H && c.DeletedAt == null && c.Status != CommissionStatus.Cancelled && c.Status != "CANCELLED"
                        && c.AppointmentId != null && bills.Any(i => i.AppointmentId == c.AppointmentId))
            .SumAsync(c => (decimal?)c.CommissionAmount, ct) ?? 0m;
        var billed = sums?.Billed ?? 0m;
        return new GlanceMoney(await PatientsAsync(from, to, ct), sums?.N ?? 0,
            moneyIn, expenses, share, Math.Round(moneyIn - expenses - share, 2), billed, Math.Round((sums?.Gross ?? 0m) - billed, 2));
    }

    private async Task<List<GlanceDay>> DaysAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var s = IstDateRange.ToUtcStart(from);
        var e = IstDateRange.ToUtcEndInclusive(to);
        var pays = await _db.Payments.AsNoTracking()
            .Where(p => p.HospitalId == H && p.CreatedAt >= s && p.CreatedAt <= e)
            .Select(p => new { p.CreatedAt, p.Amount, p.PaymentMethod }).ToListAsync(ct);
        var cs = IstDateRange.ClockStart(from);
        var ce = IstDateRange.ClockEndInclusive(to);
        var visits = await _db.Appointments.AsNoTracking()
            .Where(a => a.HospitalId == H && a.DeletedAt == null && a.Status != "CANCELLED" && a.DateTime >= cs && a.DateTime <= ce)
            .Select(a => a.DateTime).ToListAsync(ct);
        var list = new List<GlanceDay>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var day = d;
            list.Add(new GlanceDay(day,
                Math.Round(pays.Where(p => IstDateRange.ToIst(p.CreatedAt).Date == day && DayBookModes.Normalise(p.PaymentMethod) != null).Sum(p => p.Amount), 2),
                visits.Count(v => v.Date == day)));
        }
        return list;
    }

    /// <summary>Per modality: visits, and each bill's final amount split over its lines by their price.</summary>
    private async Task<Dictionary<string, (int Patients, decimal Money)>> ModalitiesAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var lines = await LiveBills(from, to)
            .SelectMany(i => i.Items.Select(it => new
            {
                i.Id, i.AppointmentId, i.TotalAmount, it.AppointmentServiceId,
                Line = it.IsFree ? 0m : it.Amount * it.Quantity,
            }))
            .ToListAsync(ct);
        var serviceIds = lines.Where(l => l.AppointmentServiceId != null).Select(l => l.AppointmentServiceId!.Value).Distinct().ToList();
        var modalityOf = serviceIds.Count == 0 ? new Dictionary<Guid, string>() : await _db.AppointmentServices.AsNoTracking()
            .Where(s => serviceIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Modality })
            .ToDictionaryAsync(s => s.Id, s => s.Modality, ct);

        var result = new Dictionary<string, (int Patients, decimal Money)>(StringComparer.OrdinalIgnoreCase);
        var visits = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var bill in lines.GroupBy(l => l.Id))
        {
            var sum = bill.Sum(l => l.Line);
            var total = bill.First().TotalAmount;
            foreach (var l in bill)
            {
                var mod = l.AppointmentServiceId is Guid sid && modalityOf.TryGetValue(sid, out var mm) && !string.IsNullOrWhiteSpace(mm)
                    ? Modality(mm) : "Other";
                var share = sum > 0 ? total * (l.Line / sum) : (bill.Count() > 0 ? total / bill.Count() : 0);
                result.TryGetValue(mod, out var cur);
                result[mod] = (cur.Patients, cur.Money + share);
                if (!visits.TryGetValue(mod, out var set)) visits[mod] = set = new HashSet<Guid>();
                set.Add(l.AppointmentId ?? l.Id);
            }
        }
        foreach (var k in result.Keys.ToList()) result[k] = (visits[k].Count, Math.Round(result[k].Money, 0));
        return result;
    }

    private static string Modality(string raw)
    {
        var m = raw.Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
        return m switch
        {
            "XRAY" or "XR" or "CR" or "DR" or "DX" => "X-Ray",
            "USG" or "US" or "ULTRASOUND" or "SONOGRAPHY" => "USG",
            "CT" or "CTSCAN" => "CT",
            "MRI" or "MR" => "MRI",
            "MAMMO" or "MAMMOGRAPHY" or "MG" => "Mammography",
            _ => raw.Trim(),
        };
    }

    private async Task<List<GlanceReferrer>> ReferrersAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = await LiveBills(from, to)
            .Where(i => i.AppointmentId != null)
            .Join(_db.Appointments.AsNoTracking().Where(a => a.HospitalId == H && a.ReferrerId != null),
                i => i.AppointmentId, a => (Guid?)a.AppointmentId, (i, a) => new { a.ReferrerId, i.AppointmentId, i.TotalAmount })
            .GroupBy(x => x.ReferrerId)
            .Select(g => new { Id = g.Key, Patients = g.Select(x => x.AppointmentId).Distinct().Count(), Billed = g.Sum(x => x.TotalAmount) })
            .OrderByDescending(x => x.Billed).Take(6)
            .ToListAsync(ct);
        if (rows.Count == 0) return new List<GlanceReferrer>();
        var ids = rows.Select(r => r.Id!.Value).ToList();
        var names = await _db.Referrers.AsNoTracking().Where(r => ids.Contains(r.ReferrerId))
            .Select(r => new { r.ReferrerId, r.Name }).ToListAsync(ct);
        var bills = LiveBills(from, to);
        var shares = await _db.ReferralCommissions.AsNoTracking()
            .Where(c => c.HospitalId == H && c.DeletedAt == null && ids.Contains(c.ReferrerId)
                        && c.Status != CommissionStatus.Cancelled && c.Status != "CANCELLED"
                        && c.AppointmentId != null && bills.Any(i => i.AppointmentId == c.AppointmentId))
            .GroupBy(c => c.ReferrerId).Select(g => new { g.Key, Sum = g.Sum(c => c.CommissionAmount) })
            .ToListAsync(ct);
        return rows.Select(r => new GlanceReferrer(r.Id,
            names.FirstOrDefault(n => n.ReferrerId == r.Id)?.Name ?? "—", r.Patients, r.Billed,
            shares.FirstOrDefault(s => s.Key == r.Id)?.Sum ?? 0m)).ToList();
    }

    private async Task<GlanceNeeds> NeedsAsync(DateTime today, int lateAfter, CancellationToken ct)
    {
        var approvals = await _db.ApprovalRequests.AsNoTracking()
            .CountAsync(a => a.HospitalId == H && a.DeletedAt == null && a.Status == "PENDING", ct);
        var unpaid = _db.Invoices.AsNoTracking()
            .Where(i => i.HospitalId == H && i.DeletedAt == null && (i.Status == "PENDING" || i.Status == "PARTIAL") && i.TotalAmount - i.PaidAmount > 0.009m);
        var all = await unpaid.GroupBy(_ => 1).Select(g => new { N = g.Count(), Amt = g.Sum(i => i.TotalAmount - i.PaidAmount) }).FirstOrDefaultAsync(ct);
        var monthAgo = DateTime.UtcNow.AddDays(-30);
        var old = await unpaid.Where(i => i.CreatedAt < monthAgo)
            .GroupBy(_ => 1).Select(g => new { N = g.Count(), Amt = g.Sum(i => i.TotalAmount - i.PaidAmount) }).FirstOrDefaultAsync(ct);

        var weekStart = today.AddDays(-7);
        var closedDays = await _db.DayCloses.AsNoTracking()
            .Where(d => d.HospitalId == H && d.BusinessDate >= weekStart && d.BusinessDate < today)
            .Select(d => d.BusinessDate).ToListAsync(ct);
        var cashDays = await _db.Payments.AsNoTracking()
            .Where(p => p.HospitalId == H && p.PaymentMethod == "CASH"
                        && p.CreatedAt >= IstDateRange.ToUtcStart(weekStart) && p.CreatedAt < IstDateRange.ToUtcStart(today))
            .Select(p => p.CreatedAt).ToListAsync(ct);
        var openDays = cashDays.Select(c => IstDateRange.ToIst(c).Date).Distinct().Count(d => !closedDays.Contains(d));

        var late = 0;
        if (_mediator != null)
            late = (await _mediator.Send(new GetOverdueAppointmentsQuery(lateAfter), ct)).Count(a => a.AcknowledgedAt == null);

        var partnersUnpaid = await _db.ReferralCommissions.AsNoTracking()
            .Where(c => c.HospitalId == H && c.DeletedAt == null && (c.Status == "UNPAID" || c.Status == "Unpaid"))
            .SumAsync(c => (decimal?)c.CommissionAmount, ct) ?? 0m;
        var toPay = await _db.Expenses.AsNoTracking()
            .Where(x => x.HospitalId == H && x.DeletedAt == null && x.Status != "Paid")
            .GroupBy(_ => 1).Select(g => new { N = g.Count(), Amt = g.Sum(x => x.Amount) }).FirstOrDefaultAsync(ct);
        var tdsNotSet = await _db.ReferralCommissions.AsNoTracking().AnyAsync(c => c.HospitalId == H && c.DeletedAt == null, ct)
                        && !await _db.HospitalBillingSettings.AsNoTracking().AnyAsync(s => s.HospitalId == H && (!s.TdsEnabled || s.TdsCommissionRate != null), ct);

        return new GlanceNeeds(approvals, all?.N ?? 0, Math.Round(all?.Amt ?? 0m, 2), old?.N ?? 0, Math.Round(old?.Amt ?? 0m, 2),
            openDays, late, Math.Round(partnersUnpaid, 2), toPay?.N ?? 0, Math.Round(toPay?.Amt ?? 0m, 2), tdsNotSet);
    }
}
