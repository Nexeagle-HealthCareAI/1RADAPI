using System.Text.Json;
using _1Rad.Application.Features.Audit.Queries;
using _1Rad.Domain.Entities;
using FluentAssertions;

namespace _1Rad.UnitTests.Features.Audit;

/// <summary>Who changed what: a bill's own changes and its payments, in plain words, newest first.</summary>
public class RecordHistoryTests : BaseHandlerTest
{
    [Fact]
    public async Task Bill_history_shows_people_and_plain_fields()
    {
        var bill = new Invoice { InvoiceId = "INV-1", HospitalId = HospitalId, PatientId = Guid.NewGuid(), PatientName = "Asha", GrossAmount = 900, TotalAmount = 900 };
        Context.Invoices.Add(bill);
        var pay = new Payment { HospitalId = HospitalId, InvoiceId = bill.Id, Amount = 800, PaymentMethod = "UPI" };
        Context.Payments.Add(pay);
        Context.Users.Add(new User { UserId = UserId, FullName = "Priya" });
        await Context.SaveChangesAsync();
        Context.AuditLogs.RemoveRange(Context.AuditLogs); // start clean: only the rows below
        Context.AuditLogs.AddRange(
            new AuditLog { HospitalId = HospitalId, UserId = UserId, EntityType = "Invoice", EntityId = bill.Id.ToString(), Action = "Modified",
                ChangesJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["CentreDiscount"] = new { from = 0, to = 100 }, ["RowVersion"] = new { from = "a", to = "b" } }),
                CreatedAt = DateTime.UtcNow.AddMinutes(-5) },
            new AuditLog { HospitalId = HospitalId, UserId = UserId, EntityType = "Payment", EntityId = pay.Id.ToString(), Action = "Added",
                ChangesJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["Amount"] = 800, ["PaymentMethod"] = "UPI", ["InvoiceId"] = bill.Id }),
                CreatedAt = DateTime.UtcNow },
            new AuditLog { HospitalId = HospitalId, UserId = UserId, EntityType = "Invoice", EntityId = Guid.NewGuid().ToString(), Action = "Modified",
                ChangesJson = "{\"Status\":{\"from\":\"PENDING\",\"to\":\"PAID\"}}", CreatedAt = DateTime.UtcNow });   // another bill
        await Context.SaveChangesAsync();

        var h = await new GetRecordHistoryQueryHandler(Context).Handle(new GetRecordHistoryQuery("bill", bill.Id), default);

        h.Should().HaveCount(2);
        h[0].What.Should().Be("Payment added");
        h[0].Who.Should().Be("Priya");
        h[0].Changes.Should().Contain(new HistoryChange("Amount", null, "800")).And.Contain(new HistoryChange("Paid by", null, "UPI"));
        h[1].Changes.Should().ContainSingle().Which.Should().Be(new HistoryChange("Discount", "0", "100"));
    }
}
