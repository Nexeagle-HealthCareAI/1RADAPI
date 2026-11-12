using _1Rad.Application.Common;
using _1Rad.Application.Features.Finance.MonthPack;
using _1Rad.Domain.Entities;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance;

/// <summary>Money ▸ Reports: the CA's month pack has a summary that ties out and one sheet per ledger.</summary>
public class MoneyPhase3MonthPackTests : BaseHandlerTest
{
    [Fact]
    public async Task MonthPack_HasTheSevenSheets_AndASummaryThatTiesOut()
    {
        var now = IstDateRange.ToIst(DateTime.UtcNow);
        var paid = new Invoice { InvoiceId = "INV2627-00001", HospitalId = HospitalId, PatientId = Guid.NewGuid(), PatientName = "A",
            GrossAmount = 900, CentreDiscount = 90, TotalAmount = 810, PaidAmount = 810, Status = "PAID", CreatedAt = DateTime.UtcNow };
        var due = new Invoice { InvoiceId = "INV2627-00002", HospitalId = HospitalId, PatientId = Guid.NewGuid(), PatientName = "B",
            GrossAmount = 1100, TotalAmount = 1100, PaidAmount = 500, Status = "PARTIAL", CreatedAt = DateTime.UtcNow };
        Context.Invoices.AddRange(paid, due);
        Context.Payments.Add(new Payment { InvoiceId = paid.Id, Amount = 810, PaymentMethod = "CASH", Kind = PaymentKinds.Payment, HospitalId = HospitalId, ReceiptNumber = "RCP-2627-00001" });
        Context.Payments.Add(new Payment { InvoiceId = due.Id, Amount = 500, PaymentMethod = "UPI", Kind = PaymentKinds.Payment, HospitalId = HospitalId, ReceiptNumber = "RCP-2627-00002" });
        await SaveChangesAsync();

        var file = await new ExportMonthPackQueryHandler(Context).Handle(new ExportMonthPackQuery(now.Year, now.Month), default);

        file.FileName.Should().Be($"Month-pack-{now:yyyy-MM}.xlsx");
        using var wb = new XLWorkbook(new MemoryStream(file.Content));
        wb.Worksheets.Select(w => w.Name).Should().Equal("Summary", "1 Sales register", "2 Receipts", "3 Credit notes", "4 Expenses",
            "5 Partner payouts", "6 Day closes", "7 Still to collect");
        var s = wb.Worksheet("Summary");
        decimal Val(string label) => s.RowsUsed().First(r => r.Cell(1).GetString() == label).Cell(2).GetValue<decimal>();
        Val("Billed (after discounts)").Should().Be(1910);
        Val("Collected on these bills").Should().Be(1310);
        Val("Still to collect on these bills").Should().Be(600);
        wb.Worksheet("2 Receipts").RowsUsed().Count().Should().Be(3);
        wb.Worksheet("7 Still to collect").RowsUsed().Count().Should().Be(2);
    }
}
