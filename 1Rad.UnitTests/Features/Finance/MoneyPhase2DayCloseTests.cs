using _1Rad.Application.Common;
using _1Rad.Application.Features.Finance.DayBook;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using FluentAssertions;
using MediatR;
using Moq;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance;

/// <summary>
/// Money redesign, phase 2 (Nov 2026): closing the day with the opening float,
/// a note-by-note count, a reason when the drawer doesn't match, the float kept
/// for tomorrow and the cash handed over.
/// </summary>
public class MoneyPhase2DayCloseTests : BaseHandlerTest
{
    private readonly DateTime _today = IstDateRange.ToIst(DateTime.UtcNow).Date;

    private IMediator Mediator()
    {
        var m = new Mock<IMediator>();
        m.Setup(x => x.Send(It.IsAny<GetDayBookQuery>(), It.IsAny<CancellationToken>()))
         .Returns((GetDayBookQuery q, CancellationToken c) => new GetDayBookQueryHandler(Context).Handle(q, c));
        return m.Object;
    }

    private async Task CashIn(decimal amount)
    {
        var inv = new Invoice { InvoiceId = "INV2627-" + Random.Shared.Next(10000, 99999), HospitalId = HospitalId, PatientId = Guid.NewGuid(),
            PatientName = "Sunita Devi", GrossAmount = amount, TotalAmount = amount, PaidAmount = amount, Status = "PAID" };
        Context.Invoices.Add(inv);
        Context.Payments.Add(new Payment { InvoiceId = inv.Id, Amount = amount, Kind = PaymentKinds.Payment, PaymentMethod = "CASH",
            HospitalId = HospitalId, CreatedAt = DateTime.UtcNow });
        await SaveChangesAsync();
    }

    private async Task Yesterday(decimal floatKept)
    {
        Context.DayCloses.Add(new DayClose { HospitalId = HospitalId, BusinessDate = _today.AddDays(-1), FloatKept = floatKept });
        await SaveChangesAsync();
    }

    private CloseDayCommandHandler Handler() => new(Context, Mediator());

    [Fact]
    public async Task OpeningFloat_IsCountedInWhatTheDrawerShouldHold()
    {
        await Yesterday(2000);
        await CashIn(500);

        var r = await Handler().Handle(new CloseDayCommand(_today, 0, null,
            NoteCounts: new() { ["500"] = 5 }, FloatKept: 1000, HandedTo: "Owner"), default);

        r.OpeningFloat.Should().Be(2000);
        r.CountedCash.Should().Be(2500);
        r.Variance.Should().Be(0);
        r.FloatKept.Should().Be(1000);
        r.HandedOver.Should().Be(1500);
        r.HandedTo.Should().Be("Owner");
        r.NoteCounts.Should().ContainKey("500");
    }

    [Fact]
    public async Task Short_WithoutAReason_IsRefused()
    {
        await Yesterday(2000);
        await CashIn(500);
        var act = () => Handler().Handle(new CloseDayCommand(_today, 0, null, NoteCounts: new() { ["2000"] = 1, ["100"] = 1, ["coins"] = 60 }), default);
        (await act.Should().ThrowAsync<ValidationException>()).Which.Message.Should().Contain("short");
    }

    [Fact]
    public async Task Short_WithAReason_IsClosedAndTheReasonKept()
    {
        await Yesterday(2000);
        await CashIn(500);
        var r = await Handler().Handle(new CloseDayCommand(_today, 0, null,
            NoteCounts: new() { ["2000"] = 1, ["100"] = 1, ["coins"] = 60 }, DifferenceReason: "Change given wrongly"), default);
        r.Variance.Should().Be(-340);
        r.DifferenceReason.Should().Be("Change given wrongly");
        r.HandedOver.Should().Be(2160);
    }

    [Fact]
    public async Task KeepingMoreThanCounted_IsRefused()
    {
        await CashIn(500);
        var act = () => Handler().Handle(new CloseDayCommand(_today, 500, null, FloatKept: 1000), default);
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task UnknownNote_IsRefused()
    {
        var act = () => Handler().Handle(new CloseDayCommand(_today, 0, null, NoteCounts: new() { ["300"] = 1 }), default);
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task DayStrip_ShowsWhichDaysAreClosed()
    {
        await Yesterday(1000);
        await CashIn(700);
        var m = new Mock<IMediator>();
        m.Setup(x => x.Send(It.IsAny<GetDayBookQuery>(), It.IsAny<CancellationToken>()))
         .Returns((GetDayBookQuery q, CancellationToken c) => new GetDayBookQueryHandler(Context).Handle(q, c));
        var strip = await new GetDayStripQueryHandler(m.Object).Handle(new GetDayStripQuery(_today, 7), default);

        strip.Should().HaveCount(7);
        strip[0].Date.Should().Be(_today);
        strip[0].Closed.Should().BeFalse();
        strip[0].CashNet.Should().Be(700);
        strip[1].Closed.Should().BeTrue();
    }

    [Fact]
    public void CashCount_AddsNotesAndCoins() =>
        CashCount.Total(new() { ["500"] = 30, ["200"] = 12, ["coins"] = 40 }).Should().Be(17440);
}
