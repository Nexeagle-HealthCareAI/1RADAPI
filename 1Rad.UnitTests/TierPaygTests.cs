using _1Rad.Application.Features.Subscriptions.Commands.ApprovePaymentRequest;
using _1Rad.Application.Features.Subscriptions.Queries.GetBillingEstimate;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace _1Rad.UnitTests;

/// <summary>Tier seat caps + PAYG (per-study) billing.</summary>
public class TierPaygTests : BaseHandlerTest
{
    // ── Seat limits ─────────────────────────────────────────────────────────
    [Fact]
    public async Task User_limit_reports_at_cap()
    {
        Context.HospitalSubscriptions.Add(new HospitalSubscription
        {
            SubscriptionId = Guid.NewGuid(), HospitalId = HospitalId, Modules = "RIS",
            Status = "Active", BillingCycle = "Monthly", MaxUsers = 2,
            EndDate = DateTime.UtcNow.AddDays(20), CreatedAt = DateTime.UtcNow,
        });
        for (int i = 0; i < 2; i++)
            Context.UserHospitalMappings.Add(new UserHospitalMapping { UserId = Guid.NewGuid(), HospitalId = HospitalId, AssignedAt = DateTime.UtcNow });
        await Context.SaveChangesAsync();

        var status = await new SubscriptionLimitsService(Context).GetUserLimitAsync(HospitalId, default);

        status.Max.Should().Be(2);
        status.Current.Should().Be(2);
        status.AtLimit.Should().BeTrue();
    }

    [Fact]
    public async Task Unlimited_cap_is_never_at_limit()
    {
        Context.HospitalSubscriptions.Add(new HospitalSubscription
        {
            SubscriptionId = Guid.NewGuid(), HospitalId = HospitalId, Modules = "RIS,PACS",
            Status = "Active", BillingCycle = "Monthly", MaxUsers = null,
            EndDate = DateTime.UtcNow.AddDays(20), CreatedAt = DateTime.UtcNow,
        });
        await Context.SaveChangesAsync();

        var status = await new SubscriptionLimitsService(Context).GetUserLimitAsync(HospitalId, default);
        status.AtLimit.Should().BeFalse();
    }

    // ── PAYG estimate ───────────────────────────────────────────────────────
    [Fact]
    public async Task Payg_estimate_is_finalized_count_times_rate()
    {
        var plan = new SubscriptionPlan
        {
            PlanId = Guid.NewGuid(), Name = "PAYG", Edition = "PACS", Modules = "PACS",
            Tier = "PAYG", BillingMode = "PerStudy", PerStudyPrice = 15m,
        };
        Context.SubscriptionPlans.Add(plan);
        Context.HospitalSubscriptions.Add(new HospitalSubscription
        {
            SubscriptionId = Guid.NewGuid(), HospitalId = HospitalId, Modules = "PACS",
            Status = "Active", BillingCycle = "Monthly", BillingMode = "PerStudy", PerStudyPrice = 15m,
            StartDate = DateTime.UtcNow.AddDays(-10), EndDate = DateTime.UtcNow.AddDays(20), CreatedAt = DateTime.UtcNow,
        });
        for (int i = 0; i < 3; i++)
            Context.DiagnosticReports.Add(new DiagnosticReport
            {
                Id = Guid.NewGuid(), HospitalId = HospitalId, Findings = "x", Impression = "y",
                IsFinalized = true, FinalizedAt = DateTime.UtcNow.AddDays(-5),
            });
        // A non-finalized report must not be billed.
        Context.DiagnosticReports.Add(new DiagnosticReport { Id = Guid.NewGuid(), HospitalId = HospitalId, Findings = "x", Impression = "y", IsFinalized = false });
        await Context.SaveChangesAsync();

        var handler = new GetBillingEstimateQueryHandler(Context, MockUserContext.Object, new Mock<IStorageMeteringService>().Object);
        var est = await handler.Handle(new GetBillingEstimateQuery { PlanId = plan.PlanId }, default);

        est.BillingMode.Should().Be("PerStudy");
        est.StudiesCount.Should().Be(3);
        est.Total.Should().Be(45m);
    }

    // ── Activation copies tier billing mode + caps ──────────────────────────
    [Fact]
    public async Task Approval_copies_billingmode_and_caps_onto_subscription()
    {
        var plan = new SubscriptionPlan
        {
            PlanId = Guid.NewGuid(), Name = "Monthly", Edition = "RIS", Modules = "RIS", Tier = "Starter",
            Price = 1999, DurationInDays = 30, BillingMode = "Subscription", PerStudyPrice = 0, MaxUsers = 2, MaxSites = 1,
        };
        Context.SubscriptionPlans.Add(plan);
        var req = new SubscriptionPaymentRequest
        {
            RequestId = Guid.NewGuid(), HospitalId = HospitalId, PlanId = plan.PlanId, PlanName = "Monthly",
            BillingCycle = "Monthly", Modules = "RIS", Amount = 1999, Status = "Pending",
            PayerName = "x", PayerContact = "x", TransactionReference = "x", PaymentMode = "UPI", CreatedAt = DateTime.UtcNow,
        };
        Context.SubscriptionPaymentRequests.Add(req);
        await Context.SaveChangesAsync();

        var handler = new ApprovePaymentRequestHandler(
            Context, MockUserContext.Object,
            new Mock<IModuleEntitlementService>().Object, new Mock<IStorageMeteringService>().Object,
            NullLogger<ApprovePaymentRequestHandler>.Instance);

        (await handler.Handle(new ApprovePaymentRequestCommand(req.RequestId, "ok"), default)).Success.Should().BeTrue();

        var sub = await GetActiveAsync();
        sub.MaxUsers.Should().Be(2);
        sub.MaxSites.Should().Be(1);
        sub.BillingMode.Should().Be("Subscription");
    }

    private async Task<HospitalSubscription> GetActiveAsync() =>
        (await Task.FromResult(Context.HospitalSubscriptions
            .Where(s => s.HospitalId == HospitalId && s.Status == "Active")
            .OrderByDescending(s => s.CreatedAt).First()));
}
