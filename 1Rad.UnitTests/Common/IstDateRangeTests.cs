using _1Rad.Application.Common;
using FluentAssertions;
using Xunit;

namespace _1Rad.UnitTests.Common;

public class IstDateRangeTests
{
    [Fact]
    public void ToUtcStart_ConvertsBareDateToMidnightIstInUtc()
    {
        var bareDate = new DateTime(2026, 9, 16);

        var result = IstDateRange.ToUtcStart(bareDate);

        // Midnight IST on the 16th is 6:30pm UTC on the 15th — NOT midnight
        // UTC on the 16th, which is what comparing the bare value directly
        // would have silently meant.
        result.Should().Be(new DateTime(2026, 9, 15, 18, 30, 0));
    }

    [Fact]
    public void ToUtcEndInclusive_ConvertsBareDateToLastInstantBeforeNextIstMidnight()
    {
        var bareDate = new DateTime(2026, 9, 16);

        var result = IstDateRange.ToUtcEndInclusive(bareDate);

        // One tick before the 17th's IST midnight (6:30pm UTC on the 16th).
        result.Should().Be(new DateTime(2026, 9, 16, 18, 29, 59, 999).AddTicks(9999));
    }

    [Fact]
    public void EarlyMorningIstInstant_FallsWithinItsOwnCalendarDayRange_NotThePreviousOne()
    {
        // 1am IST on the 16th — the exact scenario the naive (unshifted)
        // comparison got wrong: it's 7:30pm UTC on the 15th, which a bare
        // "StartDate=Sept 16" filter compared without this helper would
        // have excluded from "the 16th" entirely.
        var oneAmIstOnThe16th = new DateTime(2026, 9, 15, 19, 30, 0);

        var start = IstDateRange.ToUtcStart(new DateTime(2026, 9, 16));
        var end = IstDateRange.ToUtcEndInclusive(new DateTime(2026, 9, 16));

        (oneAmIstOnThe16th >= start && oneAmIstOnThe16th <= end).Should().BeTrue();
    }

    [Fact]
    public void EarlyMorningIstInstant_DoesNotFallWithinThePreviousCalendarDayRange()
    {
        var oneAmIstOnThe16th = new DateTime(2026, 9, 15, 19, 30, 0);

        var start = IstDateRange.ToUtcStart(new DateTime(2026, 9, 15));
        var end = IstDateRange.ToUtcEndInclusive(new DateTime(2026, 9, 15));

        (oneAmIstOnThe16th >= start && oneAmIstOnThe16th <= end).Should().BeFalse();
    }

    [Fact]
    public void TimeOfDayComponentOnTheInput_IsIgnored()
    {
        // Callers only ever have a bare calendar date to give this (a date
        // picker, or getIstDateStr()) — any time-of-day on the input must not
        // change the result.
        var withNoise = new DateTime(2026, 9, 16, 13, 45, 30);

        IstDateRange.ToUtcStart(withNoise).Should().Be(IstDateRange.ToUtcStart(new DateTime(2026, 9, 16)));
    }

    // ── IST-clock columns (Appointment.DateTime, ServiceDate) ──

    [Fact]
    public void ClockWindow_IsTheBareIstDay()
    {
        var day = new DateTime(2026, 9, 16, 13, 45, 0);
        IstDateRange.ClockStart(day).Should().Be(new DateTime(2026, 9, 16));
        IstDateRange.ClockEndInclusive(day).Should().Be(new DateTime(2026, 9, 17).AddTicks(-1));

        var oneAmVisit = new DateTime(2026, 9, 16, 1, 0, 0);       // stored as India clock time
        (oneAmVisit >= IstDateRange.ClockStart(day) && oneAmVisit <= IstDateRange.ClockEndInclusive(day)).Should().BeTrue();
        var lateVisitDayBefore = new DateTime(2026, 9, 15, 23, 30, 0);
        (lateVisitDayBefore >= IstDateRange.ClockStart(day)).Should().BeFalse();
    }

    [Fact]
    public void NormalizeToClock_ConvertsUtcAndKeepsBareValues()
    {
        IstDateRange.NormalizeToClock(new DateTime(2026, 10, 30, 4, 30, 0, DateTimeKind.Utc))
            .Should().Be(new DateTime(2026, 10, 30, 10, 0, 0));
        IstDateRange.NormalizeToClock(new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Unspecified))
            .Should().Be(new DateTime(2026, 10, 30, 10, 0, 0));
        IstDateRange.NormalizeToClock(new DateTime(2026, 10, 30, 4, 30, 0, DateTimeKind.Utc)).Kind
            .Should().Be(DateTimeKind.Unspecified);
    }

    [Fact]
    public void ClockToUtc_IsTheInverse()
    {
        var utc = IstDateRange.ClockToUtc(new DateTime(2026, 10, 30, 10, 0, 0));
        utc.Should().Be(new DateTime(2026, 10, 30, 4, 30, 0));
        utc.Kind.Should().Be(DateTimeKind.Utc);
    }
}
