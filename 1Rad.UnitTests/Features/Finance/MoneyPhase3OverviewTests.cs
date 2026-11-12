using _1Rad.Application.Common;
using _1Rad.Application.Features.Finance.Overview;
using _1Rad.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance;

/// <summary>Money ▸ Overview: the four numbers always add up.</summary>
public class MoneyPhase3OverviewTests : BaseHandlerTest
{
    private readonly DateTime _today = IstDateRange.ToIst(DateTime.UtcNow).Date;

    private Invoice Bill(decimal gross, decimal discount, decimal paid, string status, Guid? appt = null)
    {
        var inv = new Invoice { InvoiceId = "INV2627-" + Random.Shared.Next(10000, 99999), HospitalId = HospitalId, PatientId = Guid.NewGuid(),
            PatientName = "P", GrossAmount = gross, CentreDiscount = discount, TotalAmount = gross - discount, PaidAmount = paid, Status = status,
            AppointmentId = appt, CreatedAt = DateTime.UtcNow };
        inv.Items.Add(new InvoiceItem { InvoiceId = inv.Id, Description = "USG Whole Abdomen", Amount = gross });
        Context.Invoices.Add(inv);
        return inv;
    }

    [Fact]
    public async Task BilledEqualsCollectedPlusDue_AndCentreKeepsIsBilledLessShare()
    {
        var appt = Guid.NewGuid();
        Bill(900, 90, 810, "PAID", appt);
        Bill(1100, 0, 500, "PARTIAL");
        Bill(2600, 0, 0, "PENDING");
        Bill(450, 0, 0, "CANCELLED");
        Context.ReferralCommissions.Add(new ReferralCommission { HospitalId = HospitalId, ReferrerId = Guid.NewGuid(), AppointmentId = appt,
            CommissionAmount = 162, Status = "UNPAID" });
        await SaveChangesAsync();

        var o = await new GetMoneyOverviewQueryHandler(Context).Handle(new GetMoneyOverviewQuery(_today, _today), default);

        o.Bills.Should().Be(3);
        o.Cancelled.Should().Be(1);
        o.Billed.Should().Be(4510);
        o.Collected.Should().Be(1310);
        o.Due.Should().Be(3200);
        (o.Collected + o.Due).Should().Be(o.Billed);
        o.PartnerShare.Should().Be(162);
        o.CentreKeeps.Should().Be(4348);
        o.Discounts.Should().Be(90);
        o.Tests.Should().ContainSingle(t => t.Test == "USG Whole Abdomen");
    }
}
