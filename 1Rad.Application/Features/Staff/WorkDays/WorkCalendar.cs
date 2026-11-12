using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.WorkDays;

/// <summary>
/// Which days are working days at a centre (Nov 2026): the weekly off, which
/// Saturdays are off, the holiday list, and a person's own weekly off. Leave is
/// counted on working days only, and attendance and salary will use the same
/// calendar, so a Sunday or a holiday inside a leave never uses up leave.
/// </summary>
public sealed class WorkCalendar
{
    public static readonly string[] Days = { "MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN" };

    public IReadOnlySet<string> WeeklyOff { get; }
    public string? SaturdaysOff { get; }
    public IReadOnlyDictionary<DateOnly, string> Holidays { get; }

    public WorkCalendar(IEnumerable<string> weeklyOff, string? saturdaysOff, IReadOnlyDictionary<DateOnly, string> holidays)
    {
        WeeklyOff = weeklyOff.ToHashSet(StringComparer.OrdinalIgnoreCase);
        SaturdaysOff = saturdaysOff;
        Holidays = holidays;
    }

    public static string Code(DayOfWeek d) => Days[((int)d + 6) % 7];

    /// <summary>"SUN" / "sat, sun" → ["SAT","SUN"] in week order; anything unknown is refused.</summary>
    public static List<string> ParseDays(IEnumerable<string>? days)
    {
        var list = (days ?? Array.Empty<string>())
            .SelectMany(d => (d ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(d => d.ToUpperInvariant()).Distinct().ToList();
        var bad = list.Where(d => !Days.Contains(d)).ToList();
        if (bad.Count > 0) throw new ValidationException("weeklyOff", $"Not a weekday: {string.Join(", ", bad)}.");
        if (list.Count > 3) throw new ValidationException("weeklyOff", "At most three weekly off days.");
        return Days.Where(list.Contains).ToList();
    }

    public static List<string> Split(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? new List<string>() : ParseDays(new[] { stored });

    /// <summary>What a day is for someone: WORKING, WEEKLY_OFF or HOLIDAY. A personal weekly off replaces the centre's.</summary>
    public string Kind(DateOnly day, IReadOnlyCollection<string>? personalWeeklyOff = null)
    {
        if (Holidays.ContainsKey(day)) return "HOLIDAY";
        var code = Code(day.DayOfWeek);
        var off = personalWeeklyOff is { Count: > 0 } ? personalWeeklyOff : WeeklyOff;
        if (off.Contains(code, StringComparer.OrdinalIgnoreCase)) return "WEEKLY_OFF";
        // Saturdays off only follow the centre rule when the person has no weekly off of their own.
        if (code == "SAT" && personalWeeklyOff is not { Count: > 0 } && SaturdaysOff is { } rule)
        {
            var nth = (day.Day - 1) / 7 + 1;
            if ((rule == "2_4" && nth is 2 or 4) || (rule == "1_3" && nth is 1 or 3)) return "WEEKLY_OFF";
        }
        return "WORKING";
    }

    public bool IsWorking(DateOnly day, IReadOnlyCollection<string>? personalWeeklyOff = null) =>
        Kind(day, personalWeeklyOff) == "WORKING";

    public int WorkingDays(DateOnly from, DateOnly to, IReadOnlyCollection<string>? personalWeeklyOff = null)
    {
        var n = 0;
        for (var d = from; d <= to; d = d.AddDays(1)) if (IsWorking(d, personalWeeklyOff)) n++;
        return n;
    }

    /// <summary>The centre's calendar with holidays between from and to (inclusive).</summary>
    public static async Task<WorkCalendar> LoadAsync(IApplicationDbContext db, Guid hospitalId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var cal = await db.HospitalWorkCalendars.AsNoTracking().FirstOrDefaultAsync(c => c.HospitalId == hospitalId, ct);
        var holidays = await db.HospitalHolidays.AsNoTracking()
            .Where(h => h.HospitalId == hospitalId && h.HolidayDate >= from && h.HolidayDate <= to)
            .ToDictionaryAsync(h => h.HolidayDate, h => h.Name, ct);
        return new WorkCalendar(cal == null ? new[] { "SUN" } : Split(cal.WeeklyOff), cal?.SaturdaysOff, holidays);
    }
}

// ── Read the calendar for a year ───────────────────────────────────────────

public record WorkCalendarMonth(string Month, int Days, int Working, int WeeklyOff, int Holidays);
public record HolidayDto(Guid Id, DateOnly Date, string Name, string Weekday);
public record WorkCalendarDto(int Year, List<string> WeeklyOff, string? SaturdaysOff, bool Configured,
    List<HolidayDto> Holidays, List<WorkCalendarMonth> Months, int WorkingDaysInYear);

public record GetWorkCalendarQuery(int Year) : IRequest<WorkCalendarDto>;

public class GetWorkCalendarQueryHandler : IRequestHandler<GetWorkCalendarQuery, WorkCalendarDto>
{
    private readonly IApplicationDbContext _db;
    public GetWorkCalendarQueryHandler(IApplicationDbContext db) => _db = db;

    public async Task<WorkCalendarDto> Handle(GetWorkCalendarQuery q, CancellationToken ct)
    {
        if (q.Year is < 2000 or > 2100) throw new ValidationException("year", "Year looks wrong.");
        var h = _db.UserContext.HospitalId;
        var from = new DateOnly(q.Year, 1, 1);
        var to = new DateOnly(q.Year, 12, 31);
        var cal = await WorkCalendar.LoadAsync(_db, h, from, to, ct);
        var configured = await _db.HospitalWorkCalendars.AsNoTracking().AnyAsync(c => c.HospitalId == h, ct);
        var ids = await _db.HospitalHolidays.AsNoTracking()
            .Where(x => x.HospitalId == h && x.HolidayDate >= from && x.HolidayDate <= to)
            .OrderBy(x => x.HolidayDate)
            .Select(x => new { x.HolidayId, x.HolidayDate, x.Name }).ToListAsync(ct);

        var months = new List<WorkCalendarMonth>();
        for (var m = 1; m <= 12; m++)
        {
            int days = DateTime.DaysInMonth(q.Year, m), work = 0, off = 0, hol = 0;
            for (var d = 1; d <= days; d++)
            {
                switch (cal.Kind(new DateOnly(q.Year, m, d)))
                {
                    case "WORKING": work++; break;
                    case "HOLIDAY": hol++; break;
                    default: off++; break;
                }
            }
            months.Add(new WorkCalendarMonth($"{q.Year}-{m:00}", days, work, off, hol));
        }
        return new WorkCalendarDto(q.Year, cal.WeeklyOff.OrderBy(d => Array.IndexOf(WorkCalendar.Days, d)).ToList(), cal.SaturdaysOff, configured,
            ids.Select(x => new HolidayDto(x.HolidayId, x.HolidayDate, x.Name, WorkCalendar.Code(x.HolidayDate.DayOfWeek))).ToList(),
            months, months.Sum(m => m.Working));
    }
}

// ── Change the working week ────────────────────────────────────────────────

public record SaveWorkWeekCommand(List<string>? WeeklyOff, string? SaturdaysOff) : IRequest<Unit>;

public class SaveWorkWeekCommandHandler : IRequestHandler<SaveWorkWeekCommand, Unit>
{
    private readonly IApplicationDbContext _db;
    public SaveWorkWeekCommandHandler(IApplicationDbContext db) => _db = db;

    public async Task<Unit> Handle(SaveWorkWeekCommand c, CancellationToken ct)
    {
        var days = WorkCalendar.ParseDays(c.WeeklyOff);
        var sat = string.IsNullOrWhiteSpace(c.SaturdaysOff) ? null : c.SaturdaysOff.Trim();
        if (sat is not null and not ("2_4" or "1_3"))
            throw new ValidationException("saturdaysOff", "Saturdays off must be 2_4, 1_3 or empty.");
        if (sat != null && days.Contains("SAT")) sat = null; // every Saturday is already off
        var h = _db.UserContext.HospitalId;
        var row = await _db.HospitalWorkCalendars.FirstOrDefaultAsync(x => x.HospitalId == h, ct);
        if (row == null) { row = new HospitalWorkCalendar { HospitalId = h }; _db.HospitalWorkCalendars.Add(row); }
        row.WeeklyOff = string.Join(",", days);
        row.SaturdaysOff = sat;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedByUserId = _db.UserContext.UserId == Guid.Empty ? null : _db.UserContext.UserId;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ── Holidays ───────────────────────────────────────────────────────────────

public record HolidayInput(DateOnly Date, string Name);
/// <summary>Adds one or more holidays; a date that is already a holiday is renamed, not duplicated.</summary>
public record AddHolidaysCommand(List<HolidayInput> Holidays) : IRequest<int>;

public class AddHolidaysCommandHandler : IRequestHandler<AddHolidaysCommand, int>
{
    private readonly IApplicationDbContext _db;
    public AddHolidaysCommandHandler(IApplicationDbContext db) => _db = db;

    public async Task<int> Handle(AddHolidaysCommand c, CancellationToken ct)
    {
        var items = (c.Holidays ?? new()).Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Date).Select(g => g.Last()).ToList();
        if (items.Count == 0) throw new ValidationException("holidays", "Add a date and a name.");
        if (items.Count > 60) throw new ValidationException("holidays", "At most 60 holidays at a time.");
        var h = _db.UserContext.HospitalId;
        var dates = items.Select(x => x.Date).ToList();
        var existing = await _db.HospitalHolidays.Where(x => x.HospitalId == h && dates.Contains(x.HolidayDate)).ToListAsync(ct);
        var user = _db.UserContext.UserId == Guid.Empty ? (Guid?)null : _db.UserContext.UserId;
        foreach (var it in items)
        {
            var name = it.Name.Trim();
            if (name.Length > 100) name = name[..100];
            var row = existing.FirstOrDefault(x => x.HolidayDate == it.Date);
            if (row != null) row.Name = name;
            else _db.HospitalHolidays.Add(new HospitalHoliday { HospitalId = h, HolidayDate = it.Date, Name = name, CreatedByUserId = user });
        }
        await _db.SaveChangesAsync(ct);
        return items.Count;
    }
}

public record DeleteHolidayCommand(Guid HolidayId) : IRequest<Unit>;

public class DeleteHolidayCommandHandler : IRequestHandler<DeleteHolidayCommand, Unit>
{
    private readonly IApplicationDbContext _db;
    public DeleteHolidayCommandHandler(IApplicationDbContext db) => _db = db;

    public async Task<Unit> Handle(DeleteHolidayCommand c, CancellationToken ct)
    {
        var h = _db.UserContext.HospitalId;
        var row = await _db.HospitalHolidays.FirstOrDefaultAsync(x => x.HolidayId == c.HolidayId && x.HospitalId == h, ct)
                  ?? throw new NotFoundException("Holiday", c.HolidayId);
        _db.HospitalHolidays.Remove(row);
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ── A person's own weekly off ──────────────────────────────────────────────

/// <summary>Null or empty = follow the centre's weekly off.</summary>
public record SetStaffWeeklyOffCommand(Guid StaffId, List<string>? WeeklyOff) : IRequest<Unit>;

public class SetStaffWeeklyOffCommandHandler : IRequestHandler<SetStaffWeeklyOffCommand, Unit>
{
    private readonly IApplicationDbContext _db;
    public SetStaffWeeklyOffCommandHandler(IApplicationDbContext db) => _db = db;

    public async Task<Unit> Handle(SetStaffWeeklyOffCommand c, CancellationToken ct)
    {
        var h = _db.UserContext.HospitalId;
        var staff = await _db.StaffMembers.FirstOrDefaultAsync(s => s.StaffId == c.StaffId && s.HospitalId == h, ct)
                    ?? throw new NotFoundException("Staff member", c.StaffId);
        var days = WorkCalendar.ParseDays(c.WeeklyOff);
        staff.WeeklyOff = days.Count == 0 ? null : string.Join(",", days);
        staff.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ── How many leave days a range is for one person ──────────────────────────

public record LeaveDaysDto(int CalendarDays, int LeaveDays, List<string> Skipped);
public record CountLeaveDaysQuery(Guid StaffId, DateOnly From, DateOnly To) : IRequest<LeaveDaysDto>;

public class CountLeaveDaysQueryHandler : IRequestHandler<CountLeaveDaysQuery, LeaveDaysDto>
{
    private readonly IApplicationDbContext _db;
    public CountLeaveDaysQueryHandler(IApplicationDbContext db) => _db = db;

    public async Task<LeaveDaysDto> Handle(CountLeaveDaysQuery q, CancellationToken ct)
    {
        if (q.To < q.From) throw new ValidationException("toDate", "The last day is before the first day.");
        if (q.To.DayNumber - q.From.DayNumber > 366) throw new ValidationException("toDate", "Leave can be at most a year long.");
        var h = _db.UserContext.HospitalId;
        var off = await _db.StaffMembers.AsNoTracking().Where(s => s.StaffId == q.StaffId && s.HospitalId == h)
            .Select(s => new { s.WeeklyOff }).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Staff member", q.StaffId);
        var cal = await WorkCalendar.LoadAsync(_db, h, q.From, q.To, ct);
        var personal = WorkCalendar.Split(off.WeeklyOff);
        var skipped = new List<string>();
        for (var d = q.From; d <= q.To; d = d.AddDays(1))
        {
            var k = cal.Kind(d, personal);
            if (k == "HOLIDAY") skipped.Add($"{d:dd MMM} {cal.Holidays[d]}");
            else if (k == "WEEKLY_OFF") skipped.Add($"{d:dd MMM} weekly off");
        }
        var total = q.To.DayNumber - q.From.DayNumber + 1;
        return new LeaveDaysDto(total, total - skipped.Count, skipped);
    }
}
