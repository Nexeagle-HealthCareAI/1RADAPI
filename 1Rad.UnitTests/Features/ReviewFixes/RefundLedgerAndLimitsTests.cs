using _1Rad.Application.Common;
using _1Rad.Application.Features.Referrers.Queries.GetDoctorPortal;
using _1Rad.Domain.Constants;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace _1Rad.UnitTests.Features.ReviewFixes;

/// <summary>
/// Appointments / billing / referral review, phase 2 (Oct 2026): refunds as their own
/// rows (B5, B6, B7), front-desk discount limits (B3), custom-role routes (A4),
/// portal masking (R9).
/// </summary>
public class RefundLedgerTests : BaseHandlerTest
{
    private Invoice Bill(decimal total, decimal paid)
    {
        var inv = new Invoice
        {
            InvoiceId = "INV-R-" + Guid.NewGuid().ToString()[..4], HospitalId = HospitalId, PatientId = Guid.NewGuid(),
            PatientName = "A B", GrossAmount = total, TotalAmount = total, PaidAmount = paid,
            Status = paid >= total ? "PAID" : paid > 0 ? "PARTIAL" : "PENDING",
        };
        Context.Invoices.Add(inv);
        return inv;
    }

    private void Pay(Invoice inv, decimal amount, string method = "CASH") =>
        Context.Payments.Add(new Payment { InvoiceId = inv.Id, HospitalId = HospitalId, Amount = amount, PaymentMethod = method });

    [Fact]
    public async Task CashRefund_AddsANegativeRow_AndKeepsTheOriginal()
    {
        var inv = Bill(1000, 1000); Pay(inv, 1000);
        await SaveChangesAsync();

        var outcome = await PaymentLedger.RefundAsync(Context, inv, 400, "CASH", "overcharged", default);
        await SaveChangesAsync();

        var rows = await Context.Payments.Where(p => p.InvoiceId == inv.Id).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Sum(p => p.Amount).Should().Be(600);
        var refund = rows.Single(p => p.Kind == PaymentKinds.Refund);
        refund.Amount.Should().Be(-400);
        refund.PaymentMethod.Should().Be("CASH");
        refund.CreatedByUserId.Should().Be(UserId);
        inv.PaidAmount.Should().Be(600);
        inv.Status.Should().Be("PARTIAL");
        outcome.PaidOut.Should().Be(400);
        (await Context.CreditTransactions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task WalletFundedPart_GoesBackToTheWallet_WhateverModeIsAsked()
    {
        var inv = Bill(1000, 1000); Pay(inv, 300, "ADVANCE"); Pay(inv, 700, "CASH");
        await SaveChangesAsync();

        var outcome = await PaymentLedger.RefundAsync(Context, inv, 1000, "CASH", "visit cancelled", default);
        await SaveChangesAsync();

        outcome.ToWallet.Should().Be(300);
        outcome.PaidOut.Should().Be(700);
        var credit = await Context.CreditTransactions.SingleAsync();
        credit.Type.Should().Be("ADVANCE");
        credit.Amount.Should().Be(300);
        inv.PaidAmount.Should().Be(0);
    }

    [Fact]
    public async Task RefundAboveCollected_IsRefused()
    {
        var inv = Bill(1000, 500); Pay(inv, 500);
        await SaveChangesAsync();
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() =>
            PaymentLedger.RefundAsync(Context, inv, 600, "CASH", "x", default));
    }

    [Fact]
    public async Task BillLoweredBelowPaid_ParksTheExcessAsAdvance()
    {
        var inv = Bill(1000, 800); Pay(inv, 800);
        await SaveChangesAsync();

        inv.TotalAmount = 700;
        var excess = await PaymentLedger.SettleAfterTotalChangeAsync(Context, inv, "discount", default);
        await SaveChangesAsync();

        excess.Should().Be(100);
        inv.PaidAmount.Should().Be(700);
        inv.Status.Should().Be("PAID");
        (await Context.CreditTransactions.SingleAsync()).Amount.Should().Be(100);
    }

    [Fact]
    public async Task LegacyBillWithoutRows_UsesItsStoredPaidAmount()
    {
        var inv = Bill(500, 500);
        await SaveChangesAsync();
        (await PaymentLedger.CollectedAsync(Context, inv, default)).Should().Be(500);
    }

    [Fact]
    public void ZeroBill_IsPaid_AndCancelledStaysCancelled()
    {
        var zero = new Invoice { TotalAmount = 0, PaidAmount = 0, Status = "PENDING" };
        PaymentLedger.DeriveStatus(zero);
        zero.Status.Should().Be("PAID");
        var cancelled = new Invoice { TotalAmount = 100, Status = "CANCELLED" };
        PaymentLedger.DeriveStatus(cancelled);
        cancelled.Status.Should().Be("CANCELLED");
    }
}

public class DiscountPolicyTests
{
    private static _1Rad.Application.Interfaces.IUserContext User(params int[] roles)
    {
        var u = new Mock<_1Rad.Application.Interfaces.IUserContext>();
        u.Setup(x => x.RoleIds).Returns(roles);
        return u.Object;
    }

    [Fact]
    public void FrontDesk_UpToTenPercent_IsAllowed() =>
        DiscountPolicy.CheckLimit(User(5), null, 1000, 100, 0);

    [Fact]
    public void FrontDesk_AboveTheLimit_IsRefused() =>
        Assert.Throws<BusinessRuleViolationException>(() => DiscountPolicy.CheckLimit(User(5), null, 1000, 150, 0));

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(6)]
    public void AdminsAndAccountants_AreNotCapped(int role) =>
        DiscountPolicy.CheckLimit(User(role), null, 1000, 900, 50);

    [Fact]
    public void NegativeValues_AreRefused() =>
        Assert.Throws<ValidationException>(() => DiscountPolicy.RejectNegatives(-1, 0, 0));
}

public class WorkflowRouteTests
{
    [Theory]
    [InlineData("/operations-board", "/api/v1/appointments/3f2a/status", true)]
    [InlineData("/operations-board", "/api/v1/appointments/3f2a/services/9/status", true)]
    [InlineData("/operations-board", "/api/v1/appointments/3f2a", false)]          // edit = front desk
    [InlineData("/operations-board", "/api/v1/appointments/import", false)]
    [InlineData("/technician", "/api/v1/appointments/3f2a/change-referrer", false)]
    [InlineData("/appointment-board", "/api/v1/appointments/3f2a", true)]
    public void Custom_workflow_pages_move_visits_but_do_not_edit_them(string page, string path, bool granted) =>
        CustomRoleRouteMap.Grants(new[] { page }, path).Should().Be(granted);

    [Fact]
    public void Least_privileged_role_is_listed_first() =>
        RoleSets.Workflow.Split(',')[0].Should().Be(RoleConstants.Technician);
}

public class PortalMaskingTests
{
    [Theory]
    [InlineData("Ravi kumar  Singh", "R. K. S.")]
    [InlineData("", "—")]
    [InlineData("—", "—")]
    public void Agents_see_initials(string name, string expected) =>
        GetDoctorPortalQueryHandler.Initials(name).Should().Be(expected);
}
