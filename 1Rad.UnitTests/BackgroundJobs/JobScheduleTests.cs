using _1Rad.Infrastructure.BackgroundJobs;
using FluentAssertions;

namespace _1Rad.UnitTests.BackgroundJobs;

// Daily jobs run on India time even though the container clock is UTC.
public class JobScheduleTests
{
    private static DateTime Utc(string s) =>
        DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    [Fact]
    public void Nine_pm_ist_is_1530_utc()
    {
        // 15:00 UTC = 20:30 IST → next 21:00 IST is 30 minutes away.
        JobSchedule.DelayUntilIst(21, 0, Utc("2026-10-30T15:00:00Z")).Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void After_the_slot_waits_for_tomorrow()
    {
        // 16:00 UTC = 21:30 IST → next 21:00 IST is 23.5 h away.
        JobSchedule.DelayUntilIst(21, 0, Utc("2026-10-30T16:00:00Z")).Should().Be(TimeSpan.FromHours(23.5));
    }

    [Fact]
    public void Ist_today_rolls_over_at_1830_utc()
    {
        JobSchedule.IstToday(Utc("2026-10-30T18:29:00Z")).Should().Be(new DateTime(2026, 10, 30));
        JobSchedule.IstToday(Utc("2026-10-30T18:30:00Z")).Should().Be(new DateTime(2026, 10, 31));
    }
}
