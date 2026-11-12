using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.DayBook;

/// <summary>
/// The day book (review B12): only money that actually moved, grouped by IST day,
/// mode and cashier — so the drawer can be counted against it.
///
///   In : bill payments (cash/UPI/card), advances taken at the counter.
///   Out: bill refunds paid out, advance refunds, referral payouts (net of TDS),
///        expenses paid that day.
///   Not money: settling a bill from the patient's advance, or returning money to
///   the advance — those move value inside the wallet, not through the drawer.
///
/// The Revenue screen's "collected" figures are accrual (by service date); this is
/// the cash view.
/// </summary>
public record GetDayBookQuery(DateTime From, DateTime To) : IRequest<DayBookDto>;

public record DayBookLine(DateTime At, string Kind, string Mode, decimal Amount, string? Reference, string? Party, string? Cashier);
public record DayBookModeTotal(string Mode, decimal In, decimal Out, decimal Net);
public record DayBookCashierTotal(string Cashier, string Mode, decimal Net, int Entries);
public record DayCloseDto(DateTime BusinessDate, decimal ExpectedCash, decimal CountedCash, decimal Variance,
    decimal ExpectedUpi, decimal ExpectedCard, decimal ExpectedOther, string? Note, string? ClosedByName, DateTime ClosedAt, int CloseCount,
    // Money ▸ Cash book (migration 117)
    decimal OpeningFloat, decimal ExpectedBank, Dictionary<string, int>? NoteCounts, string? DifferenceReason,
    decimal FloatKept, decimal HandedOver, string? HandedTo)
{
    public static DayCloseDto From(DayClose d) => new(d.BusinessDate, d.ExpectedCash, d.CountedCash, d.Variance, d.ExpectedUpi,
        d.ExpectedCard, d.ExpectedOther, d.Note, d.ClosedByName, d.ClosedAt, d.CloseCount,
        d.OpeningFloat, d.ExpectedBank, CashCount.Parse(d.NoteCounts), d.DifferenceReason, d.FloatKept, d.HandedOver, d.HandedTo);
}

/// <summary>One day on the cash book's day strip.</summary>
public record DayStripItem(DateTime Date, bool Closed, decimal Variance, decimal CashNet, decimal AllNet, int Entries);

/// <summary>Counting the drawer note by note. Keys are the note value, or "coins" for loose coins in rupees.</summary>
public static class CashCount
{
    public static readonly int[] Notes = { 2000, 500, 200, 100, 50, 20, 10 };

    public static Dictionary<string, int>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Total of a count; throws on an unknown note or a negative number.</summary>
    public static decimal Total(Dictionary<string, int> counts)
    {
        decimal total = 0;
        foreach (var (k, n) in counts)
        {
            if (n < 0) throw new ValidationException("A count can't be negative.");
            if (n > 100000) throw new ValidationException("That count is too large.");
            if (k == "coins") { total += n; continue; }
            if (!int.TryParse(k, out var note) || !Notes.Contains(note))
                throw new ValidationException($"₹{k} is not a note we count.");
            total += note * (decimal)n;
        }
        return total;
    }
}
public record DayBookDto(DateTime From, DateTime To, List<DayBookLine> Lines, List<DayBookModeTotal> ByMode,
    List<DayBookCashierTotal> ByCashier, List<DayCloseDto> Closes,
    // Cash left in the drawer by the last close before From (migration 117).
    decimal OpeningFloat);

public static class DayBookModes
{
    public static readonly string[] Drawer = { "CASH", "UPI", "CARD" };

    /// <summary>CASH / UPI / CARD / OTHER, or null when it is not money (wallet).</summary>
    public static string? Normalise(string? mode)
    {
        var m = (mode ?? string.Empty).Trim().ToUpperInvariant();
        if (m.Length == 0 || m == PaymentLedger.WalletMode || m == "WALLET" || m == "CREDIT") return null;
        if (m is "CASH" or "UPI" or "CARD" or "BANK") return m;
        if (m is "NEFT" or "IMPS" or "RTGS" or "CHEQUE" or "BANK TRANSFER") return "BANK";
        if (m.Contains("UPI") || m.Contains("GPAY") || m.Contains("PHONEPE") || m.Contains("PAYTM")) return "UPI";
        if (m.Contains("CARD")) return "CARD";
        return "OTHER";
    }
}

public class GetDayBookQueryHandler : IRequestHandler<GetDayBookQuery, DayBookDto>
{
    private readonly IApplicationDbContext _db;
    public GetDayBookQueryHandler(IApplicationDbContext db) => _db = db;

    public async Task<DayBookDto> Handle(GetDayBookQuery q, CancellationToken ct)
    {
        var hospitalId = _db.UserContext.HospitalId;
        var from = q.From.Date;
        var to = q.To.Date < from ? from : q.To.Date;
        if ((to - from).TotalDays > 92) throw new ValidationException("The day book covers at most 3 months at a time.");
        var start = IstDateRange.ToUtcStart(from);
        var end = IstDateRange.ToUtcEndInclusive(to);

        var payments = await _db.Payments.AsNoTracking()
            .Where(p => p.HospitalId == hospitalId && p.CreatedAt >= start && p.CreatedAt <= end)
            .Select(p => new { p.CreatedAt, p.Kind, p.PaymentMethod, p.Amount, p.CreatedByUserId, Bill = p.Invoice.InvoiceId, Patient = p.Invoice.PatientName })
            .ToListAsync(ct);
        var credits = await _db.CreditTransactions.AsNoTracking()
            .Where(c => c.HospitalId == hospitalId && c.DeletedAt == null && c.CreatedAt >= start && c.CreatedAt <= end
                        && (c.Type == "ADVANCE" || c.Type == "REFUND"))
            .Select(c => new { c.CreatedAt, c.Type, c.PaymentMethod, c.Amount, c.CreatedByUserId, c.InvoiceDisplayId, c.PatientName })
            .ToListAsync(ct);
        var payouts = await _db.ReferralPayouts.AsNoTracking()
            .Where(r => r.HospitalId == hospitalId && r.PaidAt >= start && r.PaidAt <= end)
            .Select(r => new { r.PaidAt, r.Mode, r.Net, r.CreatedByUserId, r.PayoutNumber, r.PayeeName })
            .ToListAsync(ct);
        var expenses = await _db.Expenses.AsNoTracking()
            // Only money that actually left, on the day it left (migration 119).
            .Where(e => e.HospitalId == hospitalId && e.DeletedAt == null && e.Status == "Paid"
                        && (e.PaidAt ?? e.TransactionDate) >= start && (e.PaidAt ?? e.TransactionDate) <= end)
            .Select(e => new { TransactionDate = e.PaidAt ?? e.TransactionDate, e.PaymentMode, e.Amount, e.Description, e.VendorName })
            .ToListAsync(ct);

        var userIds = payments.Select(p => p.CreatedByUserId).Concat(credits.Select(c => c.CreatedByUserId))
            .Concat(payouts.Select(p => p.CreatedByUserId)).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var names = userIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.Users.AsNoTracking().IgnoreQueryFilters().Where(u => userIds.Contains(u.UserId))
                .ToDictionaryAsync(u => u.UserId, u => u.FullName, ct);
        string? Who(Guid? id) => id.HasValue && names.TryGetValue(id.Value, out var n) ? n : null;

        var lines = new List<DayBookLine>();
        foreach (var p in payments)
        {
            var mode = DayBookModes.Normalise(p.PaymentMethod);
            if (mode == null) continue; // settled from / returned to the advance — not drawer money
            var refund = p.Kind == PaymentKinds.Refund || p.Amount < 0;
            lines.Add(new DayBookLine(p.CreatedAt, refund ? "BILL_REFUND" : "BILL_PAYMENT", mode, p.Amount, p.Bill, p.Patient, Who(p.CreatedByUserId)));
        }
        foreach (var c in credits)
        {
            var mode = DayBookModes.Normalise(c.PaymentMethod);
            if (mode == null) continue;
            var inward = c.Type == "ADVANCE";
            lines.Add(new DayBookLine(c.CreatedAt, inward ? "ADVANCE_IN" : "ADVANCE_REFUND", mode, inward ? c.Amount : -c.Amount,
                c.InvoiceDisplayId, c.PatientName, Who(c.CreatedByUserId)));
        }
        foreach (var r in payouts)
        {
            if (r.Net <= 0) continue;
            lines.Add(new DayBookLine(r.PaidAt, "REFERRAL_PAYOUT", DayBookModes.Normalise(r.Mode) ?? "OTHER", -r.Net, r.PayoutNumber, r.PayeeName, Who(r.CreatedByUserId)));
        }
        foreach (var e in expenses)
        {
            if (e.Amount <= 0) continue;
            lines.Add(new DayBookLine(e.TransactionDate, "EXPENSE", DayBookModes.Normalise(e.PaymentMode) ?? "OTHER", -e.Amount,
                e.Description, e.VendorName, null));
        }
        lines = lines.OrderBy(l => l.At).ToList();

        var byMode = lines.GroupBy(l => l.Mode)
            .Select(g => new DayBookModeTotal(g.Key, g.Where(l => l.Amount > 0).Sum(l => l.Amount),
                -g.Where(l => l.Amount < 0).Sum(l => l.Amount), g.Sum(l => l.Amount)))
            .OrderBy(m => Array.IndexOf(new[] { "CASH", "UPI", "CARD", "BANK", "OTHER" }, m.Mode)).ToList();
        var byCashier = lines.Where(l => l.Kind != "EXPENSE")
            .GroupBy(l => (l.Cashier ?? "Unknown", l.Mode))
            .Select(g => new DayBookCashierTotal(g.Key.Item1, g.Key.Item2, g.Sum(l => l.Amount), g.Count()))
            .OrderBy(c => c.Cashier).ThenBy(c => c.Mode).ToList();

        var closeRows = await _db.DayCloses.AsNoTracking()
            .Where(d => d.HospitalId == hospitalId && d.BusinessDate >= from && d.BusinessDate <= to)
            .OrderBy(d => d.BusinessDate)
            .ToListAsync(ct);
        var opening = await OpeningFloatAsync(_db, hospitalId, from, ct);

        return new DayBookDto(from, to, lines, byMode, byCashier, closeRows.Select(DayCloseDto.From).ToList(), opening);
    }

    /// <summary>Cash the last close before this day left in the drawer (0 when none).</summary>
    public static async Task<decimal> OpeningFloatAsync(IApplicationDbContext db, Guid hospitalId, DateTime day, CancellationToken ct)
    {
        return await db.DayCloses.AsNoTracking()
            .Where(d => d.HospitalId == hospitalId && d.BusinessDate < day.Date)
            .OrderByDescending(d => d.BusinessDate)
            .Select(d => (decimal?)d.FloatKept)
            .FirstOrDefaultAsync(ct) ?? 0m;
    }
}

/// <summary>
/// Money ▸ Cash book day strip: the last N days up to To, each with whether it was
/// closed (and how far off the count was) and what moved.
/// </summary>
public record GetDayStripQuery(DateTime To, int Days = 7) : IRequest<List<DayStripItem>>;

public class GetDayStripQueryHandler : IRequestHandler<GetDayStripQuery, List<DayStripItem>>
{
    private readonly IMediator _mediator;
    public GetDayStripQueryHandler(IMediator mediator) => _mediator = mediator;

    public async Task<List<DayStripItem>> Handle(GetDayStripQuery q, CancellationToken ct)
    {
        var days = Math.Clamp(q.Days, 1, 31);
        var to = q.To.Date;
        var from = to.AddDays(-(days - 1));
        var book = await _mediator.Send(new GetDayBookQuery(from, to), ct);
        var byDay = book.Lines.GroupBy(l => IstDateRange.ToIst(l.At).Date).ToDictionary(g => g.Key, g => g.ToList());
        var closes = book.Closes.ToDictionary(c => c.BusinessDate.Date);
        var list = new List<DayStripItem>();
        for (var d = to; d >= from; d = d.AddDays(-1))
        {
            byDay.TryGetValue(d, out var ls);
            ls ??= new List<DayBookLine>();
            var closed = closes.TryGetValue(d, out var c);
            list.Add(new DayStripItem(d, closed, closed ? c!.Variance : 0m,
                ls.Where(l => l.Mode == "CASH").Sum(l => l.Amount), ls.Sum(l => l.Amount), ls.Count));
        }
        return list;
    }
}

/// <summary>
/// Close a day: record the counted cash against what the day book expects.
/// A day that is already closed can only be re-counted by an accountant or admin.
/// </summary>
public record CloseDayCommand(DateTime Date, decimal CountedCash, string? Note,
    // Money ▸ Cash book (migration 117). When NoteCounts is sent, the counted cash is
    // worked out from it. DifferenceReason (or a note) is required when the count
    // doesn't match. FloatKept stays in the drawer; the rest is handed over.
    Dictionary<string, int>? NoteCounts = null, string? DifferenceReason = null,
    decimal? FloatKept = null, string? HandedTo = null) : IRequest<DayCloseDto>;

public class CloseDayCommandHandler : IRequestHandler<CloseDayCommand, DayCloseDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IMediator _mediator;
    public CloseDayCommandHandler(IApplicationDbContext db, IMediator mediator) { _db = db; _mediator = mediator; }

    public async Task<DayCloseDto> Handle(CloseDayCommand cmd, CancellationToken ct)
    {
        var counted = cmd.NoteCounts is { Count: > 0 } ? CashCount.Total(cmd.NoteCounts) : cmd.CountedCash;
        if (counted < 0) throw new ValidationException("Counted cash can't be negative.");
        var date = cmd.Date.Date;
        var today = IstDateRange.ToIst(DateTime.UtcNow).Date;
        if (date > today) throw new ValidationException("You can't close a day that hasn't happened yet.");

        var book = await _mediator.Send(new GetDayBookQuery(date, date), ct);
        decimal Net(string mode) => book.ByMode.FirstOrDefault(m => m.Mode == mode)?.Net ?? 0m;

        var hospitalId = _db.UserContext.HospitalId;
        var userId = _db.UserContext.UserId;
        var name = await _db.Users.AsNoTracking().IgnoreQueryFilters().Where(u => u.UserId == userId).Select(u => u.FullName).FirstOrDefaultAsync(ct);
        var row = await _db.DayCloses.FirstOrDefaultAsync(d => d.HospitalId == hospitalId && d.BusinessDate == date, ct);
        if (row != null && !DiscountPolicy.HasFinanceRole(_db.UserContext))
            throw new ForbiddenException("This day is already closed. Ask an accountant or admin to re-count it.");

        var opening = book.OpeningFloat;
        var expectedInDrawer = opening + Net("CASH");
        var variance = Math.Round(counted, 2) - expectedInDrawer;
        var note = string.IsNullOrWhiteSpace(cmd.Note) ? null : cmd.Note.Trim();
        var reason = string.IsNullOrWhiteSpace(cmd.DifferenceReason) ? null : cmd.DifferenceReason.Trim();
        if (Math.Abs(variance) > 0.009m && reason == null && note == null)
            throw new ValidationException($"The drawer is {(variance < 0 ? "short" : "over")} by ₹{Math.Abs(variance):0.##}. Say why before closing.");
        var floatKept = Math.Round(cmd.FloatKept ?? 0m, 2);
        if (floatKept < 0) throw new ValidationException("The cash kept for tomorrow can't be negative.");
        if (floatKept > counted + 0.009m) throw new ValidationException("You can't keep more cash for tomorrow than you counted.");

        if (row == null)
        {
            row = new DayClose { HospitalId = hospitalId, BusinessDate = date, CloseCount = 0 };
            _db.DayCloses.Add(row);
        }
        row.OpeningFloat = opening;
        row.ExpectedCash = Net("CASH");
        row.ExpectedUpi = Net("UPI");
        row.ExpectedCard = Net("CARD");
        row.ExpectedBank = Net("BANK");
        row.ExpectedOther = Net("OTHER");
        row.CountedCash = Math.Round(counted, 2);
        row.Variance = variance;
        row.NoteCounts = cmd.NoteCounts is { Count: > 0 }
            ? System.Text.Json.JsonSerializer.Serialize(cmd.NoteCounts.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value))
            : null;
        row.DifferenceReason = Math.Abs(variance) > 0.009m && reason != null ? (reason.Length > 120 ? reason[..120] : reason) : null;
        row.FloatKept = floatKept;
        row.HandedOver = Math.Round(row.CountedCash - floatKept, 2);
        row.HandedTo = string.IsNullOrWhiteSpace(cmd.HandedTo) ? null : (cmd.HandedTo.Trim().Length > 100 ? cmd.HandedTo.Trim()[..100] : cmd.HandedTo.Trim());
        row.Note = row.CloseCount > 0 && row.Note != null
            ? Trim500($"{row.Note} | re-count {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC: {note ?? reason ?? "no note"}")
            : (note == null ? null : Trim500(note));
        row.ClosedByUserId = userId == Guid.Empty ? null : userId;
        row.ClosedByName = name;
        row.ClosedAt = DateTime.UtcNow;
        row.CloseCount += 1;
        await _db.SaveChangesAsync(ct);
        return DayCloseDto.From(row);
    }

    private static string Trim500(string s) => s.Length <= 500 ? s : s[..500];
}
