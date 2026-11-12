using _1Rad.Application.Features.Staff.Commands.ApplyLeave;
using _1Rad.Application.Features.Staff.LeaveBalances;
using _1Rad.Application.Features.Staff.WorkDays;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using FluentAssertions;

namespace _1Rad.UnitTests.Features.Staff;

/// <summary>
/// Staff phase 1 (Nov 2026): the centre's working days, holidays, a person's own
/// weekly off, leave counted on working days, and leave balances worked out on
/// the server with opening balances, corrections and year-end carry-forward.
/// </summary>
public class WorkDaysLeaveTests : BaseHandlerTest
{
    private readonly Guid _asha = Guid.NewGuid();   // works Mon–Sat, centre's Sunday off
    private readonly Guid _ravi = Guid.NewGuid();   // radiographer, own weekly off Tuesday
    private readonly Guid _neha = Guid.NewGuid();   // joined 1 July 2026

    private const string Policy = """
        [{"id":"cl","name":"Casual","annualQuota":12,"isPaid":true,"color":"#000","carryForwardMax":0},
         {"id":"el","name":"Earned","annualQuota":15,"isPaid":true,"carryForwardMax":10},
         {"id":"lwp","name":"Unpaid","annualQuota":0,"isPaid":false}]
        """;

    private async Task SeedAsync()
    {
        Context.Hospitals.Add(new Hospital { HospitalId = HospitalId, HospitalName = "Main Road", CreatedAt = DateTime.UtcNow });
        Context.StaffMembers.AddRange(
            new StaffMember { StaffId = _asha, HospitalId = HospitalId, FullName = "Asha Das", JoiningDate = new DateOnly(2024, 4, 1) },
            new StaffMember { StaffId = _ravi, HospitalId = HospitalId, FullName = "Ravi Kumar", JoiningDate = new DateOnly(2023, 1, 9), WeeklyOff = "TUE" },
            new StaffMember { StaffId = _neha, HospitalId = HospitalId, FullName = "Neha Sen", JoiningDate = new DateOnly(2026, 7, 1) });
        Context.HospitalLeavePolicies.Add(new HospitalLeavePolicy { HospitalId = HospitalId, LeaveTypesJson = Policy });
        await Context.SaveChangesAsync();
    }

    private Task<(Guid, string?)> Apply(Guid staff, string type, string from, string to) =>
        new ApplyLeaveCommandHandler(Context).Handle(new ApplyLeaveCommand(staff, HospitalId, type, from, to, 99, "test"), default);

    private async Task<LeaveTypeBalance> Balance(Guid staff, string type, int year = 2026) =>
        (await new GetLeaveBalancesQueryHandler(Context).Handle(new GetLeaveBalancesQuery(year, staff), default))
            .Staff.Single().Types.Single(t => t.Type == type);

    [Fact]
    public void Calendar_knows_weekly_off_second_and_fourth_saturday_holidays_and_personal_off()
    {
        var cal = new WorkCalendar(new[] { "SUN" }, "2_4",
            new Dictionary<DateOnly, string> { [new DateOnly(2026, 11, 9)] = "Diwali" });
        cal.Kind(new DateOnly(2026, 11, 1)).Should().Be("WEEKLY_OFF");   // Sunday
        cal.Kind(new DateOnly(2026, 11, 7)).Should().Be("WORKING");      // 1st Saturday
        cal.Kind(new DateOnly(2026, 11, 14)).Should().Be("WEEKLY_OFF");  // 2nd Saturday
        cal.Kind(new DateOnly(2026, 11, 28)).Should().Be("WEEKLY_OFF");  // 4th Saturday
        cal.Kind(new DateOnly(2026, 11, 9)).Should().Be("HOLIDAY");
        // A person with their own weekly off (Tuesday) works Sundays and every Saturday.
        var tue = new[] { "TUE" };
        cal.Kind(new DateOnly(2026, 11, 1), tue).Should().Be("WORKING");
        cal.Kind(new DateOnly(2026, 11, 14), tue).Should().Be("WORKING");
        cal.Kind(new DateOnly(2026, 11, 3), tue).Should().Be("WEEKLY_OFF");
        cal.Kind(new DateOnly(2026, 11, 9), tue).Should().Be("HOLIDAY");
        // November 2026: 30 days − 5 Sundays − 2 Saturdays − 1 holiday = 22.
        cal.WorkingDays(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)).Should().Be(22);
    }

    [Fact]
    public async Task Leave_over_a_weekend_and_holiday_counts_only_working_days()
    {
        await SeedAsync();
        await new AddHolidaysCommandHandler(Context).Handle(new AddHolidaysCommand(new() { new(new DateOnly(2026, 11, 9), "Diwali") }), default);

        var (id, err) = await Apply(_asha, "casual", "2026-11-07", "2026-11-10"); // Sat, Sun, Mon (Diwali), Tue
        err.Should().BeNull();
        var req = Context.StaffLeaveRequests.Single(r => r.LeaveRequestId == id);
        req.Days.Should().Be(2);              // Saturday and Tuesday
        req.LeaveType.Should().Be("Casual"); // spelt as in the policy

        // Ravi's weekly off is Tuesday, so for him the same dates are Sat, Sun = 2 as well (Mon is the holiday).
        (await new CountLeaveDaysQueryHandler(Context).Handle(new CountLeaveDaysQuery(_ravi, new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 10)), default))
            .LeaveDays.Should().Be(2);

        var (_, none) = await Apply(_asha, "Casual", "2026-11-08", "2026-11-09"); // Sunday + holiday
        none.Should().Contain("no leave is needed");
    }

    [Fact]
    public async Task Balance_counts_quota_taken_pending_and_stops_overbooking()
    {
        await SeedAsync();
        Context.StaffLeaveRequests.AddRange(
            new StaffLeaveRequest { StaffId = _asha, HospitalId = HospitalId, LeaveType = "Casual", FromDate = new DateOnly(2026, 3, 2), ToDate = new DateOnly(2026, 3, 6), Days = 5, Status = "approved" },
            new StaffLeaveRequest { StaffId = _asha, HospitalId = HospitalId, LeaveType = "Casual", FromDate = new DateOnly(2026, 6, 1), ToDate = new DateOnly(2026, 6, 3), Days = 3, Status = "pending" },
            new StaffLeaveRequest { StaffId = _asha, HospitalId = HospitalId, LeaveType = "Casual", FromDate = new DateOnly(2026, 7, 1), ToDate = new DateOnly(2026, 7, 1), Days = 1, Status = "rejected" },
            new StaffLeaveRequest { StaffId = _asha, HospitalId = HospitalId, LeaveType = "Earned", FromDate = new DateOnly(2026, 5, 31), ToDate = new DateOnly(2026, 5, 31), Days = 2, Status = "approved", Reason = "Encashed during salary payout" });
        await Context.SaveChangesAsync();

        var cl = await Balance(_asha, "Casual");
        cl.Quota.Should().Be(12); cl.Taken.Should().Be(5); cl.Pending.Should().Be(3); cl.Balance.Should().Be(7);
        (await Balance(_asha, "Earned")).Encashed.Should().Be(2);

        // 7 left, 3 already waiting → only 4 can be asked for.
        var (_, err) = await Apply(_asha, "Casual", "2026-12-01", "2026-12-05"); // Tue–Sat = 5 working days
        err.Should().Contain("Only 4 day(s) of Casual left in 2026");
        var (_, ok) = await Apply(_asha, "Casual", "2026-12-01", "2026-12-04");
        ok.Should().BeNull();

        // Unpaid leave has no balance and never blocks.
        var (_, lwp) = await Apply(_asha, "Unpaid", "2026-12-14", "2026-12-26");
        lwp.Should().BeNull();
        var (_, lwpLabel) = await Apply(_asha, "Unpaid Leave (LWP)", "2026-12-28", "2026-12-28"); // the page's built-in unpaid option
        lwpLabel.Should().BeNull();
        var (_, unknown) = await Apply(_asha, "Sabbatical", "2026-12-29", "2026-12-29");
        unknown.Should().Contain("not in the leave policy");
    }

    [Fact]
    public void Joiners_get_part_year_quota_and_monthly_accrual_follows_the_calendar()
    {
        var el = new LeaveTypeRule("Earned", 15, true, 10, false);
        LeaveBalanceCalculator.Quota(el, 2026, new DateOnly(2026, 7, 1), new DateOnly(2026, 11, 12)).Should().Be(7.5m); // 6 of 12 months
        LeaveBalanceCalculator.Quota(el, 2026, new DateOnly(2024, 4, 1), new DateOnly(2026, 11, 12)).Should().Be(15);
        LeaveBalanceCalculator.Quota(el, 2026, new DateOnly(2027, 1, 1), new DateOnly(2026, 11, 12)).Should().Be(0);
        var monthly = el with { MonthlyAccrual = true };
        LeaveBalanceCalculator.Quota(monthly, 2026, new DateOnly(2024, 4, 1), new DateOnly(2026, 11, 12)).Should().Be(13.5m); // 11 months, to the half day
        LeaveBalanceCalculator.Quota(monthly, 2025, new DateOnly(2024, 4, 1), new DateOnly(2026, 11, 12)).Should().Be(15);   // past year: full
    }

    [Fact]
    public async Task Opening_balance_replaces_corrections_add_and_need_a_reason()
    {
        await SeedAsync();
        var h = new AdjustLeaveBalanceCommandHandler(Context);
        await h.Handle(new AdjustLeaveBalanceCommand(_ravi, 2026, "earned", "OPENING", 8, null), default);
        await h.Handle(new AdjustLeaveBalanceCommand(_ravi, 2026, "Earned", "OPENING", 6, null), default);   // replaces 8
        await h.Handle(new AdjustLeaveBalanceCommand(_ravi, 2026, "Earned", "ADJUST", -1.5m, "Half day missed in register"), default);
        var el = await Balance(_ravi, "Earned");
        el.Opening.Should().Be(6); el.Adjusted.Should().Be(-1.5m); el.Balance.Should().Be(15 + 6 - 1.5m);

        await FluentActions.Invoking(() => h.Handle(new AdjustLeaveBalanceCommand(_ravi, 2026, "Earned", "ADJUST", 2, " "), default))
            .Should().ThrowAsync<ValidationException>();
        await FluentActions.Invoking(() => h.Handle(new AdjustLeaveBalanceCommand(_ravi, 2026, "Unpaid", "OPENING", 2, null), default))
            .Should().ThrowAsync<ValidationException>();
        await FluentActions.Invoking(() => h.Handle(new AdjustLeaveBalanceCommand(_ravi, 2026, "Earned", "ADJUST", 0.3m, "x"), default))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Year_end_carries_up_to_the_limit_and_can_be_rerun()
    {
        await SeedAsync();
        Context.StaffLeaveRequests.Add(new StaffLeaveRequest { StaffId = _asha, HospitalId = HospitalId, LeaveType = "Earned",
            FromDate = new DateOnly(2025, 3, 3), ToDate = new DateOnly(2025, 3, 5), Days = 3, Status = "approved" });
        await Context.SaveChangesAsync();

        var r = await new RunLeaveYearEndCommandHandler(Context).Handle(new RunLeaveYearEndCommand(2025), default);
        // Asha: Earned 15 − 3 = 12 → 10 carried, 2 lapse; Casual 12 → carry limit 0, all lapse.
        // Ravi: Earned 15 → 10 carried, 5 lapse; Casual 12 lapse. Neha joined in 2026: nothing.
        r.DaysCarried.Should().Be(20);
        r.DaysLapsed.Should().Be(2 + 12 + 5 + 12);
        (await Balance(_asha, "Earned")).CarriedIn.Should().Be(10);
        (await Balance(_asha, "Earned")).Balance.Should().Be(25);
        (await Balance(_neha, "Earned")).CarriedIn.Should().Be(0);

        await new RunLeaveYearEndCommandHandler(Context).Handle(new RunLeaveYearEndCommand(2025), default); // again
        Context.StaffLeaveLedger.Count(l => l.Kind == StaffLeaveLedgerEntry.CarryIn).Should().Be(2);
        (await new GetLeaveBalancesQueryHandler(Context).Handle(new GetLeaveBalancesQuery(2025, null), default)).YearEndDone.Should().BeTrue();
    }

    [Fact]
    public async Task Work_week_and_holidays_save_and_personal_weekly_off_can_be_cleared()
    {
        await SeedAsync();
        await new SaveWorkWeekCommandHandler(Context).Handle(new SaveWorkWeekCommand(new() { "sun" }, "2_4"), default);
        await new AddHolidaysCommandHandler(Context).Handle(new AddHolidaysCommand(new()
        {
            new(new DateOnly(2026, 1, 26), "Republic Day"), new(new DateOnly(2026, 8, 15), "Independence Day"),
        }), default);
        await new AddHolidaysCommandHandler(Context).Handle(new AddHolidaysCommand(new() { new(new DateOnly(2026, 1, 26), "Republic Day (renamed)") }), default);

        var dto = await new GetWorkCalendarQueryHandler(Context).Handle(new GetWorkCalendarQuery(2026), default);
        dto.Configured.Should().BeTrue();
        dto.WeeklyOff.Should().Equal("SUN");
        dto.Holidays.Should().HaveCount(2);
        dto.Holidays[0].Name.Should().Be("Republic Day (renamed)");
        dto.Months.Single(m => m.Month == "2026-11").Working.Should().Be(23); // no Diwali holiday added here

        await FluentActions.Invoking(() => new SaveWorkWeekCommandHandler(Context).Handle(new SaveWorkWeekCommand(new() { "FUNDAY" }, null), default))
            .Should().ThrowAsync<ValidationException>();

        await new SetStaffWeeklyOffCommandHandler(Context).Handle(new SetStaffWeeklyOffCommand(_ravi, new()), default);
        Context.StaffMembers.Single(s => s.StaffId == _ravi).WeeklyOff.Should().BeNull();

        await new DeleteHolidayCommandHandler(Context).Handle(new DeleteHolidayCommand(dto.Holidays[1].Id), default);
        Context.HospitalHolidays.Count().Should().Be(1);
    }
}
