using _1Rad.Application.Features.Finance.Commands.ApplyInvoiceDiscount;
using _1Rad.Domain.Entities;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance;

public class ApplyInvoiceDiscountCommandHandlerTests : BaseHandlerTest
{
    private readonly ApplyInvoiceDiscountCommandHandler _handler;

    public ApplyInvoiceDiscountCommandHandlerTests()
    {
        _handler = new ApplyInvoiceDiscountCommandHandler(Context);
    }

    private Invoice SeedInvoiceWithItems(decimal itemAmount = 1000m)
    {
        var invoiceId = Guid.NewGuid();
        var invoice = new Invoice
        {
            Id = invoiceId,
            InvoiceId = "INV-DRAFT-001",
            HospitalId = HospitalId,
            GrossAmount = itemAmount,
            TotalAmount = itemAmount,
            Status = "PENDING",
        };
        invoice.Items.Add(new InvoiceItem
        {
            InvoiceId = invoiceId,
            Description = "X-Ray",
            Amount = itemAmount,
            Quantity = 1,
        });
        Context.Invoices.Add(invoice);
        return invoice;
    }

    [Fact]
    public async Task Handle_SaveAsDraftWithExtraChargeAndZeroDiscounts_PersistsTheCharge()
    {
        // Mirrors exactly what the "Save as Draft" button sends when the user
        // hasn't touched centre/referrer/institutional discount: those three
        // arrive as explicit 0 (not null) because the drawer's local state
        // defaults them to 0, same as CollectPaymentCommandHandlerTests'
        // "Handle_SettlementWithExtraCharge_AppliesTheChargeExactlyOnce".
        var invoice = SeedInvoiceWithItems();
        await Context.SaveChangesAsync();

        var result = await _handler.Handle(new ApplyInvoiceDiscountCommand
        {
            InvoiceId = invoice.Id,
            DiscountAmount = 0m,
            CentreDiscount = 0m,
            ReferrerDiscount = 0m,
            InstitutionalDeduction = 0m,
            AdditionalCharges = 200m,
            AdditionalChargesReason = "[{\"reason\":\"Night Charge\",\"amount\":200}]",
            ExtraCharges = new List<ExtraChargeDetail>
            {
                new() { Reason = "Night Charge", Amount = 200m }
            }
        }, CancellationToken.None);

        Assert.True(result);

        var updated = await Context.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(updated);
        Assert.Equal(200m, updated!.AdditionalCharges);
        Assert.Equal(1200m, updated.GrossAmount);
        Assert.Equal(1200m, updated.TotalAmount);

        var charges = Context.InvoiceExtraCharges.Where(c => c.InvoiceId == invoice.Id).ToList();
        var charge = Assert.Single(charges);
        Assert.Equal("Night Charge", charge.Reason);
        Assert.Equal(200m, charge.Amount);
    }

    [Fact]
    public async Task Handle_SecondDraftSaveAddingAnotherCharge_KeepsBothCharges()
    {
        var invoice = SeedInvoiceWithItems();
        await Context.SaveChangesAsync();

        await _handler.Handle(new ApplyInvoiceDiscountCommand
        {
            InvoiceId = invoice.Id,
            DiscountAmount = 0m,
            CentreDiscount = 0m,
            ReferrerDiscount = 0m,
            InstitutionalDeduction = 0m,
            AdditionalCharges = 200m,
            AdditionalChargesReason = "[{\"reason\":\"Night Charge\",\"amount\":200}]",
            ExtraCharges = new List<ExtraChargeDetail>
            {
                new() { Reason = "Night Charge", Amount = 200m }
            }
        }, CancellationToken.None);

        // Reopen the drawer (carries the previous charge forward in local
        // state) and add a second one, then save again.
        await _handler.Handle(new ApplyInvoiceDiscountCommand
        {
            InvoiceId = invoice.Id,
            DiscountAmount = 0m,
            CentreDiscount = 0m,
            ReferrerDiscount = 0m,
            InstitutionalDeduction = 0m,
            AdditionalCharges = 300m,
            AdditionalChargesReason = "[{\"reason\":\"Night Charge\",\"amount\":200},{\"reason\":\"Doctor Visit\",\"amount\":100}]",
            ExtraCharges = new List<ExtraChargeDetail>
            {
                new() { Reason = "Night Charge", Amount = 200m },
                new() { Reason = "Doctor Visit", Amount = 100m }
            }
        }, CancellationToken.None);

        var updated = await Context.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(updated);
        Assert.Equal(300m, updated!.AdditionalCharges);
        Assert.Equal(1300m, updated.TotalAmount);

        var charges = Context.InvoiceExtraCharges.Where(c => c.InvoiceId == invoice.Id).ToList();
        Assert.Equal(2, charges.Count);
    }

    [Fact]
    public async Task Handle_ResaveWithEmptyExtraChargesList_ClearsThePreviousCharge()
    {
        var invoice = SeedInvoiceWithItems();
        await Context.SaveChangesAsync();

        await _handler.Handle(new ApplyInvoiceDiscountCommand
        {
            InvoiceId = invoice.Id,
            DiscountAmount = 0m,
            CentreDiscount = 0m,
            ReferrerDiscount = 0m,
            InstitutionalDeduction = 0m,
            AdditionalCharges = 200m,
            AdditionalChargesReason = "[{\"reason\":\"Night Charge\",\"amount\":200}]",
            ExtraCharges = new List<ExtraChargeDetail>
            {
                new() { Reason = "Night Charge", Amount = 200m }
            }
        }, CancellationToken.None);

        await _handler.Handle(new ApplyInvoiceDiscountCommand
        {
            InvoiceId = invoice.Id,
            DiscountAmount = 0m,
            CentreDiscount = 0m,
            ReferrerDiscount = 0m,
            InstitutionalDeduction = 0m,
            AdditionalCharges = 0m,
            AdditionalChargesReason = "[]",
            ExtraCharges = new List<ExtraChargeDetail>()
        }, CancellationToken.None);

        var updated = await Context.Invoices.FindAsync(invoice.Id);
        Assert.NotNull(updated);
        Assert.Equal(0m, updated!.AdditionalCharges);
        Assert.Empty(Context.InvoiceExtraCharges.Where(c => c.InvoiceId == invoice.Id));
    }
}
