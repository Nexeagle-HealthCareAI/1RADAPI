using _1Rad.Application.Features.Subscriptions.Commands.ApprovePaymentRequest;
using _1Rad.Application.Features.Subscriptions.Queries.GetBillingEstimate;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace _1Rad.UnitTests;

/// <summary>Pure metered-storage overage math.</summary>
public class BillingMathTests
{
    private static SubscriptionPlan Plan(int? includedGb, decimal perGb, decimal price = 1000m) =>
        new() { PlanId = Guid.NewGuid(), Name = "Monthly", Edition = "PACS", Modules = "PACS", Price = price, IncludedStorageGb = includedGb, PerGbOveragePrice = perGb };

    private static StorageUsage Usage(double gb) => new() { UsedBytes = (long)(gb * 1024 * 1024 * 1024) };

    [Fact]
    public void Ris_plan_never_charges_storage()
    {
        var e = BillingMath.Estimate(Plan(includedGb: null, perGb: 0m, price: 2999m), Usage(500));
        e.OverageGb.Should().Be(0);
        e.OverageAmount.Should().Be(0m);
        e.Total.Should().Be(2999m);
    }

    [Fact]
    public void Under_allowance_is_base_only()
    {
        var e = BillingMath.Estimate(Plan(includedGb: 100, perGb: 50m), Usage(60));
        e.OverageGb.Should().Be(0);
        e.Total.Should().Be(1000m);
    }

    [Fact]
    public void Over_allowance_bills_ceil_gb_times_price()
    {
        var e = BillingMath.Estimate(Plan(includedGb: 100, perGb: 50m), Usage(150));
        e.OverageGb.Should().Be(50);
        e.OverageAmount.Should().Be(2500m);
        e.Total.Should().Be(3500m);
    }

    [Fact]
    public void Fractional_overage_rounds_up()
    {
        var e = BillingMath.Estimate(Plan(includedGb: 100, perGb: 50m), Usage(100.3));
        e.OverageGb.Should().Be(1);
        e.Total.Should().Be(1050m);
    }

    [Fact]
    public void Zero_per_gb_price_never_charges()
    {
        var e = BillingMath.Estimate(Plan(includedGb: 100, perGb: 0m), Usage(500));
        e.OverageGb.Should().Be(0);
        e.Total.Should().Be(1000m);
    }
}

/// <summary>
/// ApprovePaymentRequest must activate the purchased EDITION (the bug it fixes)
/// and start/clear the PACS downgrade grace clock.
/// </summary>
public class ApprovePaymentEditionTests : BaseHandlerTest
{
    private ApprovePaymentRequestHandler Handler() => new(
        Context, MockUserContext.Object,
        new Mock<IModuleEntitlementService>().Object,
        new Mock<IStorageMeteringService>().Object,
        NullLogger<ApprovePaymentRequestHandler>.Instance);

    private async Task<(SubscriptionPlan plan, SubscriptionPaymentRequest req)> SeedAsync(
        string planEdition, string planModules, int? includedGb, string existingModules)
    {
        var plan = new SubscriptionPlan
        {
            PlanId = Guid.NewGuid(), Name = "Monthly", Edition = planEdition, Modules = planModules,
            Price = 2999, DurationInDays = 30, IncludedStorageGb = includedGb, PerGbOveragePrice = 50,
        };
        Context.SubscriptionPlans.Add(plan);
        Context.HospitalSubscriptions.Add(new HospitalSubscription
        {
            SubscriptionId = Guid.NewGuid(), HospitalId = HospitalId, Modules = existingModules,
            Status = "Active", BillingCycle = "Monthly", EndDate = DateTime.UtcNow.AddDays(5), CreatedAt = DateTime.UtcNow,
        });
        var req = new SubscriptionPaymentRequest
        {
            RequestId = Guid.NewGuid(), HospitalId = HospitalId, PlanId = plan.PlanId, PlanName = "Monthly",
            BillingCycle = "Monthly", Modules = planModules, Amount = 2999, Status = "Pending",
            PayerName = "x", PayerContact = "x", TransactionReference = "x", PaymentMode = "UPI", CreatedAt = DateTime.UtcNow,
        };
        Context.SubscriptionPaymentRequests.Add(req);
        await Context.SaveChangesAsync();
        return (plan, req);
    }

    private async Task<HospitalSubscription> NewActiveAsync() =>
        await Context.HospitalSubscriptions
            .Where(s => s.HospitalId == HospitalId && s.Status == "Active")
            .OrderByDescending(s => s.CreatedAt).FirstAsync();

    [Fact]
    public async Task Activates_the_purchased_edition_not_full_product()
    {
        var (plan, req) = await SeedAsync("RIS", "RIS", includedGb: null, existingModules: "RIS,PACS");

        var res = await Handler().Handle(new ApprovePaymentRequestCommand(req.RequestId, "ok"), CancellationToken.None);

        res.Success.Should().BeTrue();
        var sub = await NewActiveAsync();
        sub.Modules.Should().Be("RIS");                 // the bug: was silently "RIS,PACS"
        sub.IncludedStorageGb.Should().BeNull();
        sub.PlanId.Should().Be(plan.PlanId);
    }

    [Fact]
    public async Task Dropping_pacs_starts_the_grace_clock()
    {
        var (_, req) = await SeedAsync("RIS", "RIS", includedGb: null, existingModules: "RIS,PACS");

        await Handler().Handle(new ApprovePaymentRequestCommand(req.RequestId, "ok"), CancellationToken.None);

        (await NewActiveAsync()).PacsRemovedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Adding_pacs_has_no_grace()
    {
        var (_, req) = await SeedAsync("RIS+PACS", "RIS,PACS", includedGb: 50, existingModules: "RIS");

        await Handler().Handle(new ApprovePaymentRequestCommand(req.RequestId, "ok"), CancellationToken.None);

        var sub = await NewActiveAsync();
        sub.PacsRemovedAt.Should().BeNull();
        sub.IncludedStorageGb.Should().Be(50);
    }
}
