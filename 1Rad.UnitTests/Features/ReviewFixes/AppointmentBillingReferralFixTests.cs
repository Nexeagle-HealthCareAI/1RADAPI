using _1Rad.Application.Common;
using _1Rad.Application.Features.Appointments;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointment;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentServiceStatus;
using _1Rad.Domain.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace _1Rad.UnitTests.Features.ReviewFixes;

/// <summary>
/// Appointments / billing / referral review (Oct 2026): the pure rules behind the fixes.
/// </summary>
public class ServiceStatusTransitionTests
{
    [Theory]
    [InlineData("NOT_STARTED", "IN_PROGRESS")]
    [InlineData("NOT_STARTED", "DELIVERED")]   // X-ray straight to delivered
    [InlineData("SCANNED", "REPORTED")]
    [InlineData("SCANNED", "IN_MID")]           // one step back
    [InlineData("IN_MID", "NOT_STARTED")]       // mid-scan abandon
    [InlineData("IN_PROGRESS", "CANCELLED")]
    [InlineData("REPORTED", "REPORTED")]
    public void Allowed(string from, string to) =>
        UpdateAppointmentServiceStatusCommandHandler.TransitionError(from, to).Should().BeNull();

    [Theory]
    [InlineData("CANCELLED", "IN_PROGRESS")]    // A21: cancelled line can't be restarted
    [InlineData("REPORTED", "CANCELLED")]
    [InlineData("DELIVERED", "CANCELLED")]
    [InlineData("REPORTED", "SCANNED")]
    [InlineData("DELIVERED", "REPORTED")]
    [InlineData("SCANNED", "NOT_STARTED")]      // two steps back
    public void Refused(string from, string to) =>
        UpdateAppointmentServiceStatusCommandHandler.TransitionError(from, to).Should().NotBeNullOrWhiteSpace();
}

public class ServicesUnchangedTests
{
    private static UpdateAppointmentCommand Cmd(IReadOnlyList<AppointmentServiceLine>? lines = null, decimal? amount = null) =>
        new(Guid.NewGuid(), "CT Brain", "CT", DateTime.Today, "Dr A", Amount: amount, Services: lines);

    private static List<AppointmentService> Existing(params (string name, string mod, decimal amt, decimal cut)[] rows) =>
        rows.Select(r => new AppointmentService { ServiceName = r.name, Modality = r.mod, Amount = r.amt, ReferralCutValue = r.cut }).ToList();

    [Fact]
    public void SameLines_IsUnchanged()
    {
        var existing = Existing(("CT Brain", "CT", 2000, 300), ("X-Ray Chest", "XR", 400, 50));
        var lines = new[] { new AppointmentServiceLine("ct brain", "CT", 2000, 300, existing[0].Id),
                            new AppointmentServiceLine("X-Ray Chest", "XR", 400, 50) };
        UpdateAppointmentCommandHandler.ServicesUnchanged(Cmd(lines), existing).Should().BeTrue();
    }

    [Fact]
    public void PriceChange_IsAChange()
    {
        var existing = Existing(("CT Brain", "CT", 2000, 300));
        var lines = new[] { new AppointmentServiceLine("CT Brain", "CT", 1800, 300, existing[0].Id) };
        UpdateAppointmentCommandHandler.ServicesUnchanged(Cmd(lines), existing).Should().BeFalse();
    }

    [Fact]
    public void AddedLine_IsAChange()
    {
        var existing = Existing(("CT Brain", "CT", 2000, 300));
        var lines = new[] { new AppointmentServiceLine("CT Brain", "CT", 2000, 300, existing[0].Id),
                            new AppointmentServiceLine("MRI Spine", "MR", 6000, 800) };
        UpdateAppointmentCommandHandler.ServicesUnchanged(Cmd(lines), existing).Should().BeFalse();
    }

    [Fact]
    public void ScalarEdit_SameAmount_IsUnchanged()
    {
        var existing = Existing(("CT Brain", "CT", 2000, 300));
        UpdateAppointmentCommandHandler.ServicesUnchanged(Cmd(amount: 2000), existing).Should().BeTrue();
        UpdateAppointmentCommandHandler.ServicesUnchanged(Cmd(amount: 2500), existing).Should().BeFalse();
    }
}

public class ReferrerDiscountCommissionTests : BaseHandlerTest
{
    private readonly Guid _appointmentId = Guid.NewGuid();
    private readonly Guid _referrerId = Guid.NewGuid();

    private Invoice NewInvoice(decimal gross, decimal referrerDiscount) => new()
    {
        InvoiceId = "INV-T-1", AppointmentId = _appointmentId, HospitalId = HospitalId,
        GrossAmount = gross, ReferrerDiscount = referrerDiscount,
        DiscountAmount = referrerDiscount, TotalAmount = gross - referrerDiscount,
    };

    private ReferralCommission Row(decimal amount, string status = "UNPAID") => new()
    {
        ReferrerId = _referrerId, ReferrerName = "Dr Ref", AppointmentId = _appointmentId,
        HospitalId = HospitalId, CommissionAmount = amount, Status = status,
        TransactionDate = DateTime.UtcNow.AddMinutes(-Context.ReferralCommissions.Local.Count),
    };

    [Fact]
    public async Task DiscountComesOffUnpaidRows_InProportion()
    {
        Context.ReferralCommissions.AddRange(Row(300), Row(100));
        await SaveChangesAsync();
        var invoice = NewInvoice(2400, 200);

        var outcome = await ReferrerDiscountCommission.ApplyAsync(Context, invoice, 0, false, "test", default);

        outcome.RowsAdjusted.Should().Be(2);
        var rows = Context.ReferralCommissions.Local.OrderByDescending(c => c.CommissionAmount).ToList();
        rows.Sum(r => r.CommissionAmount).Should().Be(200);
        rows[0].CommissionAmount.Should().Be(150);
        rows[1].CommissionAmount.Should().Be(50);
    }

    [Fact]
    public async Task PaidRowsAreNeverEdited_AdjustmentRowIsBooked()
    {
        var paid = Row(300, "PAID");
        Context.ReferralCommissions.Add(paid);
        await SaveChangesAsync();
        var invoice = NewInvoice(2000, 100);

        var outcome = await ReferrerDiscountCommission.ApplyAsync(Context, invoice, 0, false, "test", default);
        await SaveChangesAsync();

        outcome.AdjustmentRowAdded.Should().BeTrue();
        paid.CommissionAmount.Should().Be(300);
        var adj = await Context.ReferralCommissions.SingleAsync(c => c.Id != paid.Id);
        adj.CommissionAmount.Should().Be(-100);
        adj.Status.Should().Be("UNPAID");
    }

    [Fact]
    public async Task ExcessOverCommission_MovesToCentre_WhenAbsorbing()
    {
        Context.ReferralCommissions.Add(Row(300));
        await SaveChangesAsync();
        var invoice = NewInvoice(2000, 500);

        var outcome = await ReferrerDiscountCommission.ApplyAsync(Context, invoice, 0, true, "test", default);

        outcome.Absorbed.Should().BeTrue();
        outcome.ExcessAbsorbed.Should().Be(200);
        invoice.ReferrerDiscount.Should().Be(300);
        invoice.CentreDiscount.Should().Be(200);
        invoice.TotalAmount.Should().Be(1500);
        Context.ReferralCommissions.Local.Single().CommissionAmount.Should().Be(0);
    }

    [Fact]
    public async Task NoChange_DoesNothing()
    {
        Context.ReferralCommissions.Add(Row(300));
        await SaveChangesAsync();
        var outcome = await ReferrerDiscountCommission.ApplyAsync(Context, NewInvoice(2000, 100), 100, false, "test", default);
        outcome.Changed.Should().BeFalse();
        Context.ReferralCommissions.Local.Single().CommissionAmount.Should().Be(300);
    }
}
