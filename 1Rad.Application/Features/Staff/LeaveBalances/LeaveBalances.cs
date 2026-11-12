using System.Text.Json;
using _1Rad.Application.Common;
using _1Rad.Application.Features.Staff.WorkDays;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.LeaveBalances;

/// <summary>One leave type from the centre's leave policy (HospitalLeavePolicies.LeaveTypesJson).</summary>
public record LeaveTypeRule(string Name, decimal AnnualQuota, bool IsPaid, decimal CarryForwardMax, bool MonthlyAccrual);

public static class LeavePolicyRules
{
    /// <summary>
    /// Reads the policy JSON written by the Leave policy editor:
    /// [{ id, name, annualQuota, isPaid, color, carryForwardMax?, accrual?: "yearly"|"monthly" }].
    /// </summary>
    public static List<LeaveTypeRule> Parse(string? json)
    {
        var list = new List<LeaveTypeRule>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                var name = Str(t, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                list.Add(new LeaveTypeRule(name.Trim(), Num(t, "annualQuota"),
                    !t.TryGetProperty("isPaid", out var p) || p.ValueKind != JsonValueKind.False,
                    Num(t, "carryForwardMax"),
                    string.Equals(Str(t, "accrual"), "monthly", StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (JsonException) { /* a broken policy reads as no leave types */ }
        return list;
    }

    private static string? Str(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal Num(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDecimal(out var d) => Math.Max(0, d),
            JsonValueKind.String when decimal.TryParse(v.GetString(), out var s) => Math.Max(0, s),
            _ => 0,
        } : 0;

    public static async Task<List<LeaveTypeRule>> LoadAsync(IApplicationDbContext db, Guid hospitalId, CancellationToken ct) =>
        Parse(await db.HospitalLeavePolicies.AsNoTracking().Where(p => p.HospitalId == hospitalId)
            .Select(p => p.LeaveTypesJson).FirstOrDefaultAsync(ct));
}

public record LeaveTypeBalance(
    string Type, bool Paid, decimal Quota, decimal CarriedIn, decimal Opening, decimal Adjusted,
    decimal Taken, decimal Encashed, decimal Pending, decimal Balance, decimal CarryForwardMax);

public record StaffLeaveBalance(Guid StaffId, string Name, string? EmployeeCode, string? Designation,
    DateOnly? JoiningDate, List<string> WeeklyOff, List<LeaveTypeBalance> Types);

public record LeaveBalancesDto(int Year, bool PolicySet, List<LeaveTypeRule> Policy, List<StaffLeaveBalance> Staff, bool YearEndDone);

/// <summary>
/// Leave balances worked out on the server (Nov 2026), so every screen and the
/// payroll agree. For each person, year and leave type:
///   quota     — the policy's yearly days; part-year for someone who joined this
///               year; with monthly accrual, only the months reached so far;
///   + carried in from last year, + opening balance, ± corrections (StaffLeaveLedger);
///   − taken    — approved leave, counted on that person's working days in the year;
///   − encashed — days paid out with salary;
///   pending    — waiting for approval (not yet taken off).
/// Unpaid leave types have no balance: only what was taken is shown.
/// </summary>
public static class LeaveBalanceCalculator
{
    public static bool IsEncashment(StaffLeaveRequest r) =>
        r.SourceDisbursementId != null || string.Equals(r.Reason, "Encashed during salary payout", StringComparison.OrdinalIgnoreCase);

    public static async Task<List<StaffLeaveBalance>> ComputeAsync(IApplicationDbContext db, Guid hospitalId, int year,
        IReadOnlyCollection<Guid>? onlyStaff, List<LeaveTypeRule> rules, DateOnly today, CancellationToken ct)
    {
        var from = new DateOnly(year, 1, 1);
        var to = new DateOnly(year, 12, 31);
        var staffQ = db.StaffMembers.AsNoTracking().Where(s => s.HospitalId == hospitalId);
        if (onlyStaff != null) staffQ = staffQ.Where(s => onlyStaff.Contains(s.StaffId));
        else staffQ = staffQ.Where(s => s.Status != "Inactive");
        var staff = await staffQ.OrderBy(s => s.FullName)
            .Select(s => new { s.StaffId, s.FullName, s.EmployeeCode, s.Designation, s.JoiningDate, s.WeeklyOff }).ToListAsync(ct);
        var ids = staff.Select(s => s.StaffId).ToList();

        var requests = await db.StaffLeaveRequests.AsNoTracking()
            .Where(r => r.HospitalId == hospitalId && ids.Contains(r.StaffId) && r.FromDate <= to && r.ToDate >= from
                        && (r.Status == "approved" || r.Status == "pending"))
            .ToListAsync(ct);
        var ledger = await db.StaffLeaveLedger.AsNoTracking()
            .Where(l => l.HospitalId == hospitalId && l.LeaveYear == year && ids.Contains(l.StaffId))
            .ToListAsync(ct);
        var cal = await WorkCalendar.LoadAsync(db, hospitalId, from, to, ct);

        var result = new List<StaffLeaveBalance>();
        foreach (var s in staff)
        {
            var personal = WorkCalendar.Split(s.WeeklyOff);
            var mine = requests.Where(r => r.StaffId == s.StaffId).ToList();
            var types = new List<LeaveTypeBalance>();
            foreach (var rule in rules)
            {
                bool Same(string t) => string.Equals(t?.Trim(), rule.Name, StringComparison.OrdinalIgnoreCase);
                var quota = Quota(rule, year, s.JoiningDate, today);
                var rows = ledger.Where(l => l.StaffId == s.StaffId && Same(l.LeaveType)).ToList();
                var carried = rows.Where(l => l.Kind == StaffLeaveLedgerEntry.CarryIn).Sum(l => l.Days);
                var opening = rows.Where(l => l.Kind == StaffLeaveLedgerEntry.Opening).Sum(l => l.Days);
                var adjusted = rows.Where(l => l.Kind == StaffLeaveLedgerEntry.Adjust).Sum(l => l.Days);
                decimal taken = 0, encashed = 0, pending = 0;
                foreach (var r in mine.Where(r => Same(r.LeaveType)))
                {
                    if (IsEncashment(r))
                    {
                        if (r.Status == "approved" && r.FromDate.Year == year) encashed += r.Days;
                        continue;
                    }
                    var a = r.FromDate < from ? from : r.FromDate;
                    var b = r.ToDate > to ? to : r.ToDate;
                    var days = cal.WorkingDays(a, b, personal);
                    if (r.Status == "approved") taken += days; else pending += days;
                }
                var balance = rule.IsPaid ? quota + carried + opening + adjusted - taken - encashed : 0;
                types.Add(new LeaveTypeBalance(rule.Name, rule.IsPaid, quota, carried, opening, adjusted,
                    taken, encashed, pending, balance, rule.CarryForwardMax));
            }
            result.Add(new StaffLeaveBalance(s.StaffId, s.FullName, s.EmployeeCode, s.Designation, s.JoiningDate, personal, types));
        }
        return result;
    }

    /// <summary>The yearly quota for this person and year, in half days.</summary>
    public static decimal Quota(LeaveTypeRule rule, int year, DateOnly? joined, DateOnly today)
    {
        if (!rule.IsPaid || rule.AnnualQuota <= 0) return 0;
        if (joined is { } j && j.Year > year) return 0;
        // Months of the year this person is entitled to: from joining (in the joining year) …
        var firstMonth = joined is { } jj && jj.Year == year ? jj.Month : 1;
        // … to the current month (monthly accrual, current year only) or December.
        var lastMonth = rule.MonthlyAccrual && today.Year == year ? today.Month : (rule.MonthlyAccrual && today.Year < year ? 0 : 12);
        var months = Math.Max(0, lastMonth - firstMonth + 1);
        if (months >= 12) return rule.AnnualQuota;
        return Math.Floor(rule.AnnualQuota * months / 12m * 2m) / 2m;
    }
}

// ── Read balances ──────────────────────────────────────────────────────────

public record GetLeaveBalancesQuery(int Year, Guid? StaffId) : IRequest<LeaveBalancesDto>;

public class GetLeaveBalancesQueryHandler : IRequestHandler<GetLeaveBalancesQuery, LeaveBalancesDto>
{
    private readonly IApplicationDbContext _db;
    public GetLeaveBalancesQueryHandler(IApplicationDbContext db) => _db = db;

    public async Task<LeaveBalancesDto> Handle(GetLeaveBalancesQuery q, CancellationToken ct)
    {
        if (q.Year is < 2000 or > 2100) throw new ValidationException("year", "Year looks wrong.");
        var h = _db.UserContext.HospitalId;
        var rules = await LeavePolicyRules.LoadAsync(_db, h, ct);
        var today = DateOnly.FromDateTime(IstDateRange.ToIst(DateTime.UtcNow));
        var staff = await LeaveBalanceCalculator.ComputeAsync(_db, h, q.Year, q.StaffId is { } id ? new[] { id } : null, rules, today, ct);
        var done = await _db.StaffLeaveLedger.AsNoTracking()
            .AnyAsync(l => l.HospitalId == h && l.LeaveYear == q.Year + 1 && l.Kind == StaffLeaveLedgerEntry.CarryIn, ct);
        return new LeaveBalancesDto(q.Year, rules.Count > 0, rules, staff, done);
    }
}

// ── Opening balance or a correction ────────────────────────────────────────

/// <summary>
/// Kind OPENING sets the opening balance (replaces any earlier one for that year
/// and type); ADJUST adds a correction (+ or −) with a reason.
/// </summary>
public record AdjustLeaveBalanceCommand(Guid StaffId, int Year, string LeaveType, string Kind, decimal Days, string? Note) : IRequest<Unit>;

public class AdjustLeaveBalanceCommandHandler : IRequestHandler<AdjustLeaveBalanceCommand, Unit>
{
    private readonly IApplicationDbContext _db;
    public AdjustLeaveBalanceCommandHandler(IApplicationDbContext db) => _db = db;

    public async Task<Unit> Handle(AdjustLeaveBalanceCommand c, CancellationToken ct)
    {
        var h = _db.UserContext.HospitalId;
        var kind = (c.Kind ?? string.Empty).Trim().ToUpperInvariant();
        if (kind is not (StaffLeaveLedgerEntry.Opening or StaffLeaveLedgerEntry.Adjust))
            throw new ValidationException("kind", "Kind must be OPENING or ADJUST.");
        if (c.Year is < 2000 or > 2100) throw new ValidationException("year", "Year looks wrong.");
        if (c.Days * 2 != Math.Floor(c.Days * 2)) throw new ValidationException("days", "Use whole or half days.");
        if (Math.Abs(c.Days) > 365) throw new ValidationException("days", "That is more than a year of leave.");
        if (kind == StaffLeaveLedgerEntry.Opening && c.Days < 0) throw new ValidationException("days", "An opening balance cannot be below zero.");
        if (kind == StaffLeaveLedgerEntry.Adjust && c.Days == 0) throw new ValidationException("days", "Enter the days to add or take away.");
        if (kind == StaffLeaveLedgerEntry.Adjust && string.IsNullOrWhiteSpace(c.Note)) throw new ValidationException("note", "Say why the balance is being corrected.");

        var rules = await LeavePolicyRules.LoadAsync(_db, h, ct);
        var rule = rules.FirstOrDefault(r => string.Equals(r.Name, c.LeaveType?.Trim(), StringComparison.OrdinalIgnoreCase))
                   ?? throw new ValidationException("leaveType", "That leave type is not in the leave policy.");
        if (!rule.IsPaid) throw new ValidationException("leaveType", "Unpaid leave has no balance to set.");
        if (!await _db.StaffMembers.AnyAsync(s => s.StaffId == c.StaffId && s.HospitalId == h, ct))
            throw new NotFoundException("Staff member", c.StaffId);

        var user = _db.UserContext.UserId == Guid.Empty ? (Guid?)null : _db.UserContext.UserId;
        if (kind == StaffLeaveLedgerEntry.Opening)
        {
            var old = await _db.StaffLeaveLedger.Where(l => l.HospitalId == h && l.StaffId == c.StaffId && l.LeaveYear == c.Year
                                                            && l.Kind == StaffLeaveLedgerEntry.Opening && l.LeaveType == rule.Name).ToListAsync(ct);
            _db.StaffLeaveLedger.RemoveRange(old);
        }
        _db.StaffLeaveLedger.Add(new StaffLeaveLedgerEntry
        {
            StaffId = c.StaffId, HospitalId = h, LeaveYear = c.Year, LeaveType = rule.Name, Kind = kind,
            Days = c.Days, Note = Clip(c.Note),
            CreatedByUserId = user,
        });
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }

    private static string? Clip(string? note)
    {
        var t = note?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > 300 ? t[..300] : t;
    }
}

// ── Year end: carry leave into the next year ───────────────────────────────

public record LeaveYearEndResult(int Year, int Staff, decimal DaysCarried, decimal DaysLapsed);

/// <summary>
/// Closes a leave year: for every person and paid leave type, what is left
/// (up to the type's carry-forward limit) is carried into the next year; the
/// rest lapses. Running it again replaces the earlier carry-forward, so it is
/// safe to re-run after late corrections.
/// </summary>
public record RunLeaveYearEndCommand(int Year) : IRequest<LeaveYearEndResult>;

public class RunLeaveYearEndCommandHandler : IRequestHandler<RunLeaveYearEndCommand, LeaveYearEndResult>
{
    private readonly IApplicationDbContext _db;
    public RunLeaveYearEndCommandHandler(IApplicationDbContext db) => _db = db;

    public async Task<LeaveYearEndResult> Handle(RunLeaveYearEndCommand c, CancellationToken ct)
    {
        var h = _db.UserContext.HospitalId;
        var today = DateOnly.FromDateTime(IstDateRange.ToIst(DateTime.UtcNow));
        if (c.Year < 2000 || c.Year > today.Year)
            throw new ValidationException("year", "Only a year that has started can be closed.");
        if (c.Year == today.Year && today.Month < 12)
            throw new ValidationException("year", "A leave year can be closed from December onwards.");
        var rules = await LeavePolicyRules.LoadAsync(_db, h, ct);
        if (rules.Count == 0) throw new ValidationException("policy", "Set up the leave policy first.");

        // Year-end balances use the full year's quota, whatever the accrual.
        var closing = await LeaveBalanceCalculator.ComputeAsync(_db, h, c.Year, null, rules, new DateOnly(c.Year, 12, 31), ct);
        var old = await _db.StaffLeaveLedger
            .Where(l => l.HospitalId == h && l.LeaveYear == c.Year + 1 && l.Kind == StaffLeaveLedgerEntry.CarryIn).ToListAsync(ct);
        _db.StaffLeaveLedger.RemoveRange(old);

        var user = _db.UserContext.UserId == Guid.Empty ? (Guid?)null : _db.UserContext.UserId;
        decimal carried = 0, lapsed = 0;
        foreach (var s in closing)
            foreach (var t in s.Types.Where(t => t.Paid && t.Balance > 0))
            {
                var carry = Math.Min(t.Balance, t.CarryForwardMax);
                lapsed += t.Balance - carry;
                if (carry <= 0) continue;
                carried += carry;
                _db.StaffLeaveLedger.Add(new StaffLeaveLedgerEntry
                {
                    StaffId = s.StaffId, HospitalId = h, LeaveYear = c.Year + 1, LeaveType = t.Type,
                    Kind = StaffLeaveLedgerEntry.CarryIn, Days = carry, Note = $"Carried from {c.Year}", CreatedByUserId = user,
                });
            }
        await _db.SaveChangesAsync(ct);
        return new LeaveYearEndResult(c.Year, closing.Count, carried, lapsed);
    }
}
