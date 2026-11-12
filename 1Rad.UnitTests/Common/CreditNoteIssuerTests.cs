using _1Rad.Application.Common;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.UnitTests.Common;

// Credit notes are raised by SaveChanges itself, whichever command changed the bill.
public class CreditNoteIssuerTests : BaseHandlerTest
{
    private async Task<Invoice> PaidBillAsync(decimal amount, decimal gstRate = 0)
    {
        var inv = new Invoice
        {
            HospitalId = HospitalId, PatientId = Guid.NewGuid(), PatientName = "Asha",
            InvoiceId = "INV2627-00042", GrossAmount = amount, TotalAmount = amount, Status = "PENDING",
        };
        inv.Items.Add(new InvoiceItem { InvoiceId = inv.Id, Description = "USG", Amount = amount, Quantity = 1, GstRate = gstRate });
        Context.Invoices.Add(inv);
        Context.Payments.Add(new Payment { InvoiceId = inv.Id, HospitalId = HospitalId, Amount = amount, PaymentMethod = "CASH" });
        inv.PaidAmount = amount;
        PaymentLedger.DeriveStatus(inv);
        await Context.SaveChangesAsync();
        return inv;
    }

    [Fact]
    public async Task Lowering_a_paid_bill_issues_a_credit_note_with_the_refund()
    {
        var inv = await PaidBillAsync(1000);
        Assert.Empty(await Context.CreditNotes.ToListAsync());

        inv.TotalAmount = 800;
        await PaymentLedger.SettleAfterTotalChangeAsync(Context, inv, "Concession", CancellationToken.None, "CASH");
        await Context.SaveChangesAsync();

        var cn = Assert.Single(await Context.CreditNotes.ToListAsync());
        Assert.Equal(CreditNoteKinds.CreditNote, cn.Kind);
        Assert.StartsWith("CN", cn.Number);
        Assert.Equal(200m, cn.Amount);
        Assert.Equal(200m, cn.RefundedAmount);
        Assert.Equal("CASH", cn.RefundModes);
        Assert.Equal(1000m, cn.BillTotalBefore);
        Assert.Equal(800m, cn.BillTotalAfter);
    }

    [Fact]
    public async Task Cancelling_a_taxable_paid_bill_splits_out_the_gst()
    {
        var inv = await PaidBillAsync(1180, 18);
        inv.Status = "CANCELLED";
        await Context.SaveChangesAsync();

        var cn = Assert.Single(await Context.CreditNotes.ToListAsync());
        Assert.Equal(1180m, cn.Amount);
        Assert.Equal(180m, cn.TaxAmount);
        Assert.Equal(1000m, cn.TaxableValue);
    }

    [Fact]
    public async Task A_refund_without_a_value_change_issues_a_refund_voucher()
    {
        var inv = await PaidBillAsync(600);
        await PaymentLedger.RefundAsync(Context, inv, 100, "UPI", "Overcharged", CancellationToken.None);
        await Context.SaveChangesAsync();

        var rv = Assert.Single(await Context.CreditNotes.ToListAsync());
        Assert.Equal(CreditNoteKinds.RefundVoucher, rv.Kind);
        Assert.StartsWith("RV", rv.Number);
        Assert.Equal(100m, rv.Amount);
    }

    [Fact]
    public async Task Lowering_an_unpaid_bill_is_just_a_revised_bill()
    {
        var inv = new Invoice { HospitalId = HospitalId, PatientId = Guid.NewGuid(), InvoiceId = "INV2627-00043", TotalAmount = 900, Status = "PENDING" };
        Context.Invoices.Add(inv);
        await Context.SaveChangesAsync();
        inv.TotalAmount = 500;
        await Context.SaveChangesAsync();
        Assert.Empty(await Context.CreditNotes.ToListAsync());
    }
}
