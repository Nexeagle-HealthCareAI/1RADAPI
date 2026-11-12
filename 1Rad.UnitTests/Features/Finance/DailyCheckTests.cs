using _1Rad.Application.Common;
using _1Rad.Application.Features.Finance.DailyCheck;
using _1Rad.Domain.Entities;
using FluentAssertions;

namespace _1Rad.UnitTests.Features.Finance;

/// <summary>Money ▸ Daily check: each kind of mismatch is found, and a clean day passes every check.</summary>
public class DailyCheckTests : BaseHandlerTest
{
    private readonly DateTime _today = IstDateRange.ToIst(DateTime.UtcNow).Date;

    private Appointment Visit(string status, string name = "P", Guid? patient = null)
    {
        var a = new Appointment { HospitalId = HospitalId, PatientId = patient ?? Guid.NewGuid(), PatientName = name, Status = status,
            DateTime = _today.AddHours(10), ArrivedAt = status == "BOOKED" ? null : DateTime.UtcNow, DisplayId = "APP-" + Random.Shared.Next(100, 999) };
        Context.Appointments.Add(a);
        return a;
    }

    private Invoice Bill(Appointment? a, decimal total, decimal paid, string status = "PENDING", string test = "USG Whole Abdomen")
    {
        var i = new Invoice { InvoiceId = "INV-" + Random.Shared.Next(10000, 99999), HospitalId = HospitalId, PatientId = a?.PatientId ?? Guid.NewGuid(),
            PatientName = a?.PatientName ?? "P", AppointmentId = a?.AppointmentId, GrossAmount = total, TotalAmount = total, PaidAmount = paid,
            Status = status, CreatedAt = DateTime.UtcNow };
        i.Items.Add(new InvoiceItem { InvoiceId = i.Id, Description = test, Amount = total });
        Context.Invoices.Add(i);
        if (paid > 0) Context.Payments.Add(new Payment { HospitalId = HospitalId, InvoiceId = i.Id, Amount = paid, PaymentMethod = "CASH", CreatedAt = DateTime.UtcNow });
        return i;
    }

    private Task<DailyCheckDto> Run() => new GetDailyCheckQueryHandler(Context).Handle(new GetDailyCheckQuery(_today), default);

    [Fact]
    public async Task A_clean_day_passes_every_check()
    {
        var a = Visit("COMPLETED");
        Bill(a, 900, 900, "PAID");
        Visit("BOOKED"); // not arrived yet: no bill needed
        await SaveChangesAsync();

        var r = await Run();

        r.Issues.Should().BeEmpty();
        r.Passed.Should().Contain(new[] { "NO_BILL", "PAID_MISMATCH", "DUPLICATE" });
        r.Counts.Visits.Should().Be(2);
        r.Counts.Bills.Should().Be(1);
        r.Counts.MoneyIn.Should().Be(900);
    }

    [Fact]
    public async Task Each_mismatch_is_found()
    {
        Visit("COMPLETED", "No Bill");                                   // NO_BILL
        var cancelled = Visit("CANCELLED", "Cancelled");
        Bill(cancelled, 1100, 0);                                        // CANCELLED_VISIT
        Context.ReferralCommissions.Add(new ReferralCommission { HospitalId = HospitalId, ReferrerId = Guid.NewGuid(), ReferrerName = "Dr. A",
            AppointmentId = cancelled.AppointmentId, CommissionAmount = 110, Status = "UNPAID" });   // SHARE_ON_CANCELLED
        var wrongPaid = Visit("COMPLETED", "Wrong Paid");
        var b = Bill(wrongPaid, 2600, 1000, "PARTIAL");
        b.PaidAmount = 1500;                                             // PAID_MISMATCH: rows say 1000
        var twice = Guid.NewGuid();
        var v1 = Visit("COMPLETED", "Twice", twice); var v2 = Visit("COMPLETED", "Twice", twice);
        Bill(v1, 450, 450, "PAID", "X-Ray Chest PA"); Bill(v2, 450, 450, "PAID", "x-ray chest pa ");   // DUPLICATE
        var odd = Visit("COMPLETED", "Odd Total");
        var o = Bill(odd, 1000, 0); o.GrossAmount = 1200;                // TOTAL_MISMATCH: 1200 − 0 ≠ 1000
        await SaveChangesAsync();

        var r = await Run();

        r.Issues.Select(i => i.Kind).Should().Contain(new[] { "NO_BILL", "CANCELLED_VISIT", "SHARE_ON_CANCELLED", "PAID_MISMATCH", "DUPLICATE", "TOTAL_MISMATCH" });
        r.Issues.Single(i => i.Kind == "NO_BILL").PatientName.Should().Be("No Bill");
        r.Issues.Single(i => i.Kind == "PAID_MISMATCH").Amount.Should().Be(-500);
        r.Issues.First().Severity.Should().Be("high");
    }
}
