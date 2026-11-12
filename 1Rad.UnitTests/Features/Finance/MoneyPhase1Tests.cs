using _1Rad.Application.Common;
using _1Rad.Application.Features.Finance.Commands.CollectPayment;
using _1Rad.Application.Features.Finance.Queries.GetInvoiceHistory;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance;

/// <summary>
/// Money redesign, phase 1 (Nov 2026): split cash + UPI under one receipt number,
/// role-based discount limits, a reason kept with the discount, and a bill's
/// money history.
/// </summary>
public class MoneyPhase1Tests : BaseHandlerTest
{
    private Invoice Bill(decimal total)
    {
        var inv = new Invoice
        {
            InvoiceId = "INV2627-" + Random.Shared.Next(10000, 99999), HospitalId = HospitalId, PatientId = Guid.NewGuid(),
            PatientName = "Sunita Devi", GrossAmount = total, TotalAmount = total, Status = "PENDING",
        };
        inv.Items.Add(new InvoiceItem { InvoiceId = inv.Id, Description = "USG Whole Abdomen", Amount = total });
        Context.Invoices.Add(inv);
        return inv;
    }

    private CollectPaymentCommandHandler Handler(IConfiguration? config = null) => new(Context, config);

    [Fact]
    public async Task SplitPayment_TwoRows_OneReceipt()
    {
        var inv = Bill(765); await SaveChangesAsync();

        await Handler().Handle(new CollectPaymentCommand
        {
            InvoiceId = inv.Id,
            Tenders = new() { new() { Mode = "cash", Amount = 500 }, new() { Mode = "UPI", Amount = 265, Reference = "4317 9921 0034" } },
        }, default);

        var rows = await Context.Payments.Where(p => p.InvoiceId == inv.Id).OrderBy(p => p.Amount).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(r => r.PaymentMethod).Should().BeEquivalentTo(new[] { "UPI", "CASH" });
        rows.Select(r => r.ReceiptNumber).Distinct().Should().ContainSingle().Which.Should().StartWith("RCP-");
        rows.Single(r => r.PaymentMethod == "UPI").Reference.Should().Be("4317 9921 0034");
        inv.Status.Should().Be("PAID");
        inv.PaidAmount.Should().Be(765);
    }

    [Fact]
    public async Task CashAboveTheBill_TheRestIsHeldAsAdvance()
    {
        var inv = Bill(765); await SaveChangesAsync();

        await Handler().Handle(new CollectPaymentCommand
        {
            InvoiceId = inv.Id,
            Tenders = new() { new() { Mode = "CASH", Amount = 1000 } },
        }, default);

        (await Context.Payments.SumAsync(p => p.Amount)).Should().Be(765);
        var adv = await Context.CreditTransactions.SingleAsync();
        adv.Type.Should().Be("ADVANCE");
        adv.Amount.Should().Be(235);
    }

    [Fact]
    public async Task UnknownMode_IsRefused()
    {
        var inv = Bill(500); await SaveChangesAsync();
        var act = () => Handler().Handle(new CollectPaymentCommand
        {
            InvoiceId = inv.Id,
            Tenders = new() { new() { Mode = "BITCOIN", Amount = 500 } },
        }, default);
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task ReceiptNumbers_RunInSeries()
    {
        var a = Bill(300); var b = Bill(400); await SaveChangesAsync();
        await Handler().Handle(new CollectPaymentCommand { InvoiceId = a.Id, Amount = 300, PaymentMethod = "CASH" }, default);
        await Handler().Handle(new CollectPaymentCommand { InvoiceId = b.Id, Amount = 400, PaymentMethod = "CASH" }, default);
        var nums = await Context.Payments.OrderBy(p => p.ReceiptNumber).Select(p => p.ReceiptNumber!).ToListAsync();
        nums.Should().HaveCount(2);
        nums[0].Should().NotBe(nums[1]);
    }

    [Fact]
    public async Task Discount_KeepsTheReasonAndWhoGaveIt()
    {
        var inv = Bill(900); await SaveChangesAsync();
        await Handler().Handle(new CollectPaymentCommand
        {
            InvoiceId = inv.Id, CentreDiscount = 90, DiscountReason = "Senior citizen",
            Tenders = new() { new() { Mode = "CASH", Amount = 810 } },
        }, default);
        inv.DiscountReason.Should().Be("Senior citizen");
        inv.DiscountByUserId.Should().Be(UserId);
        inv.TotalAmount.Should().Be(810);
        inv.Status.Should().Be("PAID");
    }

    [Fact]
    public async Task ReasonRequired_DiscountWithoutOne_IsRefused()
    {
        Context.HospitalBillingSettings.Add(new HospitalBillingSettings { HospitalId = HospitalId, DiscountReasonRequired = true });
        var inv = Bill(900); await SaveChangesAsync();
        var act = () => Handler().Handle(new CollectPaymentCommand { InvoiceId = inv.Id, CentreDiscount = 50 }, default);
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task FrontDesk_AboveTheLimit_AsksForApproval()
    {
        var inv = Bill(900); await SaveChangesAsync();
        var act = () => Handler().Handle(new CollectPaymentCommand { InvoiceId = inv.Id, CentreDiscount = 135, DiscountReason = "x" }, default);
        (await act.Should().ThrowAsync<BusinessRuleViolationException>()).Which.Message.Should().Contain("approval");
    }

    [Fact]
    public async Task History_ShowsTheBillAndEachPayment_InOrder()
    {
        var inv = Bill(765); await SaveChangesAsync();
        await Handler().Handle(new CollectPaymentCommand
        {
            InvoiceId = inv.Id,
            Tenders = new() { new() { Mode = "CASH", Amount = 500 }, new() { Mode = "UPI", Amount = 265 } },
        }, default);

        var h = await new GetInvoiceHistoryQueryHandler(Context).Handle(new GetInvoiceHistoryQuery(inv.Id), default);
        h.Events.First().Kind.Should().Be(InvoiceHistoryKinds.Made);
        h.Events.Count(e => e.Kind == InvoiceHistoryKinds.Payment).Should().Be(2);
        h.Balance.Should().Be(0);
        h.HasMoney.Should().BeTrue();
        h.Lines.Should().ContainSingle(l => l.Description == "USG Whole Abdomen");
    }
}

public class RoleDiscountLimitTests
{
    private static _1Rad.Application.Interfaces.IUserContext User(params int[] roles)
    {
        var u = new Mock<_1Rad.Application.Interfaces.IUserContext>();
        u.Setup(x => x.RoleIds).Returns(roles);
        return u.Object;
    }

    private static IConfiguration Cfg(string? accountant) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:FrontDeskDiscountLimitPercent"] = "10",
            ["Billing:AccountantDiscountLimitPercent"] = accountant,
        }).Build();

    [Fact]
    public void Accountant_WithALimitSet_IsCapped() =>
        Assert.Throws<BusinessRuleViolationException>(() => DiscountPolicy.CheckLimit(User(6), Cfg("20"), 1000, 250, 0));

    [Fact]
    public void Accountant_WithinTheLimit_IsAllowed() =>
        DiscountPolicy.CheckLimit(User(6), Cfg("20"), 1000, 200, 0);

    [Fact]
    public void Accountant_NoLimitSet_IsNotCapped() =>
        DiscountPolicy.CheckLimit(User(6), Cfg(null), 1000, 900, 0);

    [Fact]
    public void Admin_IsNeverCapped() =>
        DiscountPolicy.CheckLimit(User(1, 6), Cfg("5"), 1000, 1000, 0);

    [Fact]
    public void LimitFor_FrontDeskIsTheFrontDeskLimit() =>
        DiscountPolicy.LimitPercentFor(User(5), Cfg("20")).Should().Be(10);

    [Fact]
    public void ReceiptFormat_IsShortEnoughForGst() =>
        ReceiptNumbering.Format("2026-27", 4431).Should().Be("RCP-2627-04431");
}
