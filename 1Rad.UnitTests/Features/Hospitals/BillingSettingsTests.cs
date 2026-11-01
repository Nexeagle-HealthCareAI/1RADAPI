using _1Rad.Application.Common;
using _1Rad.Application.Features.Hospitals.BillingSettings;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.UnitTests.Features.Hospitals;

public class BillingSettingsTests : BaseHandlerTest
{
    [Fact]
    public async Task No_row_means_defaults()
    {
        var dto = await new BillingSettingsHandlers(Context).Handle(new GetBillingSettingsQuery(HospitalId), CancellationToken.None);
        Assert.True(dto.GstEnabled);
        Assert.True(dto.TdsEnabled);
        Assert.True(dto.CreditNotesEnabled);
        Assert.False(dto.Customised);
        Assert.Equal(10m, dto.FrontDeskDiscountLimitPercent);
    }

    [Fact]
    public async Task Saving_overrides_and_reading_back()
    {
        var h = new BillingSettingsHandlers(Context);
        var dto = await h.Handle(new UpdateBillingSettingsCommand(HospitalId, false, true, false, "professional",
            5m, null, null, null, null, 20m), CancellationToken.None);
        Assert.False(dto.GstEnabled);
        Assert.False(dto.TdsEnabled);
        Assert.Equal("PROFESSIONAL", dto.DefaultTdsCategory);
        Assert.Equal(5m, dto.TdsCommissionRate);
        Assert.Equal(20000m, dto.TdsCommissionThreshold);   // left empty → default
        Assert.Equal(20m, dto.FrontDeskDiscountLimitPercent);
        Assert.True(dto.Customised);
    }

    [Fact]
    public async Task Out_of_range_values_are_refused()
    {
        var h = new BillingSettingsHandlers(Context);
        await Assert.ThrowsAsync<_1Rad.Domain.Exceptions.ValidationException>(() => h.Handle(new UpdateBillingSettingsCommand(HospitalId, true, true, true, "COMMISSION",
            80m, null, null, null, null, null), CancellationToken.None));
        await Assert.ThrowsAsync<_1Rad.Domain.Exceptions.ValidationException>(() => h.Handle(new UpdateBillingSettingsCommand(HospitalId, true, true, true, "SALARY",
            null, null, null, null, null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Gst_switched_off_bills_every_line_at_zero()
    {
        Context.HospitalBillingSettings.Add(new HospitalBillingSettings { HospitalId = HospitalId, GstEnabled = false });
        await Context.SaveChangesAsync();
        var inv = new Invoice { HospitalId = HospitalId, PatientId = Guid.NewGuid(), InvoiceId = "INV2627-00050", TotalAmount = 1180 };
        inv.Items.Add(new InvoiceItem { InvoiceId = inv.Id, Description = "Film", Amount = 1180, Quantity = 1 });
        Context.Invoices.Add(inv);
        await Context.SaveChangesAsync();
        var item = await Context.Set<InvoiceItem>().SingleAsync();
        Assert.Equal(0m, item.GstRate);
    }
}
