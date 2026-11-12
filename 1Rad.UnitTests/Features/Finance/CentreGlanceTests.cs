using _1Rad.Application.Common;
using _1Rad.Application.Features.Finance.Overview;
using _1Rad.Domain.Entities;
using FluentAssertions;

namespace _1Rad.UnitTests.Features.Finance;

/// <summary>
/// Money ▸ Overview as "the centre at a glance": one money idea (money in, as the
/// Cash book counts it), visits as patients, modalities that add up to the bills,
/// and earned = money in − expenses paid − partners' share.
/// </summary>
public class CentreGlanceTests : BaseHandlerTest
{
    private readonly DateTime _today = IstDateRange.ToIst(DateTime.UtcNow).Date;

    private Guid Visit(string status = "COMPLETED", int daysAgo = 0)
    {
        var a = new Appointment { HospitalId = HospitalId, PatientId = Guid.NewGuid(), PatientName = "P", Status = status,
            DateTime = _today.AddDays(-daysAgo).AddHours(10) };
        Context.Appointments.Add(a);
        return a.AppointmentId;
    }

    private Invoice Bill(Guid appt, decimal total, decimal paid, params (string Mod, decimal Price)[] lines)
    {
        var inv = new Invoice { InvoiceId = "INV-" + Random.Shared.Next(10000, 99999), HospitalId = HospitalId, PatientId = Guid.NewGuid(),
            PatientName = "P", GrossAmount = lines.Sum(l => l.Price), TotalAmount = total, PaidAmount = paid,
            Status = paid >= total ? "PAID" : paid > 0 ? "PARTIAL" : "PENDING", AppointmentId = appt, CreatedAt = DateTime.UtcNow };
        foreach (var (mod, price) in lines)
        {
            var svc = new AppointmentService { HospitalId = HospitalId, AppointmentId = appt, ServiceName = mod + " test", Modality = mod, Amount = price };
            Context.AppointmentServices.Add(svc);
            inv.Items.Add(new InvoiceItem { InvoiceId = inv.Id, Description = mod + " test", Amount = price, AppointmentServiceId = svc.Id });
        }
        Context.Invoices.Add(inv);
        return inv;
    }

    private void Pay(decimal amount, string mode, int daysAgo = 0) =>
        Context.Payments.Add(new Payment { HospitalId = HospitalId, InvoiceId = Guid.NewGuid(), Amount = amount, PaymentMethod = mode,
            CreatedAt = DateTime.UtcNow.AddDays(-daysAgo) });

    [Fact]
    public async Task Today_counts_visits_and_real_money_only()
    {
        var a1 = Visit(); var a2 = Visit(); Visit("CANCELLED"); Visit(daysAgo: 1);
        Bill(a1, 900, 900, ("USG", 900));
        Bill(a2, 2600, 1000, ("CT", 2600));
        Pay(900, "CASH"); Pay(1000, "UPI"); Pay(300, "ADVANCE"); Pay(-100, "CASH");
        await SaveChangesAsync();

        var g = await new GetCentreGlanceQueryHandler(Context).Handle(new GetCentreGlanceQuery(), default);

        g.Today.Patients.Should().Be(2, "cancelled visits don't count and yesterday's is yesterday's");
        g.Today.Yesterday.Patients.Should().Be(1);
        g.Today.MoneyIn.Should().Be(1800, "wallet moves are not money in; refunds come off");
        g.Today.ByMode.Should().Contain(new GlanceMode("CASH", 800));
        g.Today.CashInDrawer.Should().Be(800);
        g.Today.ToCollect.Should().Be(1600);
        g.Today.Bills.Should().Be(2);
    }

    [Fact]
    public async Task Modalities_add_up_to_the_bills_and_earned_is_money_in_less_costs()
    {
        var a1 = Visit();
        // One visit, two modalities, ₹500 off the whole bill: split by line price.
        Bill(a1, 3000, 3000, ("USG", 1000), ("X-RAY", 2500));
        var a2 = Visit();
        Bill(a2, 500, 500, ("XR", 500));
        Pay(3500, "CASH");
        Context.Expenses.Add(new Expense { HospitalId = HospitalId, Amount = 700, Status = "Paid", Description = "Gel", Category = "Reagents", TransactionDate = DateTime.UtcNow });
        Context.Expenses.Add(new Expense { HospitalId = HospitalId, Amount = 9999, Status = "Pending", Description = "AC", Category = "Maintenance", TransactionDate = DateTime.UtcNow });
        Context.ReferralCommissions.Add(new ReferralCommission { HospitalId = HospitalId, ReferrerId = Guid.NewGuid(), AppointmentId = a1, CommissionAmount = 300, Status = "UNPAID" });
        await SaveChangesAsync();

        var g = await new GetCentreGlanceQueryHandler(Context).Handle(new GetCentreGlanceQuery(), default);

        g.Modalities.Sum(m => m.Money).Should().Be(3500);
        var xray = g.Modalities.Single(m => m.Modality == "X-Ray");
        xray.Money.Should().Be(Math.Round(3000m * 2500 / 3500) + 500);
        xray.Patients.Should().Be(2);
        g.Modalities.Single(m => m.Modality == "USG").Patients.Should().Be(1);

        g.Month.Now.MoneyIn.Should().Be(3500);
        g.Month.Now.Expenses.Should().Be(700, "only paid expenses are a cost already met");
        g.Month.Now.PartnerShare.Should().Be(300);
        g.Month.Now.Earned.Should().Be(2500);
        g.Needs.ExpensesToPay.Should().Be(1);
        g.Needs.PartnersUnpaid.Should().Be(300);
    }
}
