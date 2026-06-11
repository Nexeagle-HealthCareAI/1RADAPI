using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Subscriptions.Queries.GetBillingEstimate;

/// <summary>
/// What a center would pay for <see cref="PlanId"/> right now: the plan's base
/// price plus metered PACS storage overage (per GB over the included allowance,
/// from current usage). Used by the renew/upgrade UI.
/// </summary>
public class GetBillingEstimateQuery : IRequest<BillingEstimateDto>
{
    public Guid PlanId { get; init; }
}

public class BillingEstimateDto
{
    public bool Found { get; set; }
    public string? Error { get; set; }
    public Guid PlanId { get; set; }
    public string Edition { get; set; } = string.Empty;
    public string Cycle { get; set; } = string.Empty;       // Monthly | Yearly
    public decimal BasePrice { get; set; }
    public int? IncludedStorageGb { get; set; }
    public double UsedGb { get; set; }
    public int OverageGb { get; set; }
    public decimal PerGbPrice { get; set; }
    public decimal OverageAmount { get; set; }
    public decimal Total { get; set; }
}

public class GetBillingEstimateQueryHandler : IRequestHandler<GetBillingEstimateQuery, BillingEstimateDto>
{
    private readonly IApplicationDbContext _context;
    private readonly IUserContext _userContext;
    private readonly IStorageMeteringService _storage;

    public GetBillingEstimateQueryHandler(IApplicationDbContext context, IUserContext userContext, IStorageMeteringService storage)
    {
        _context = context;
        _userContext = userContext;
        _storage = storage;
    }

    public async Task<BillingEstimateDto> Handle(GetBillingEstimateQuery request, CancellationToken cancellationToken)
    {
        var plan = await _context.SubscriptionPlans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.PlanId == request.PlanId, cancellationToken);
        if (plan == null)
            return new BillingEstimateDto { Found = false, Error = "Plan not found." };

        return BillingMath.Estimate(plan, await _storage.GetUsageAsync(_userContext.HospitalId, cancellationToken));
    }
}

/// <summary>Pure overage math — shared by the estimate query and the submit
/// command so they agree on the amount, and unit-testable in isolation.</summary>
public static class BillingMath
{
    public static BillingEstimateDto Estimate(_1Rad.Domain.Entities.SubscriptionPlan plan, StorageUsage usage)
    {
        var usedGb = usage.UsedBytes / (1024.0 * 1024 * 1024);

        int overageGb = 0;
        decimal overageAmount = 0m;
        // Storage is only billed when the plan grants an allowance (PACS editions)
        // and a per-GB price is set.
        if (plan.IncludedStorageGb.HasValue && plan.PerGbOveragePrice > 0m)
        {
            var over = usedGb - plan.IncludedStorageGb.Value;
            if (over > 0) overageGb = (int)System.Math.Ceiling(over);
            overageAmount = overageGb * plan.PerGbOveragePrice;
        }

        return new BillingEstimateDto
        {
            Found = true,
            PlanId = plan.PlanId,
            Edition = plan.Edition,
            Cycle = plan.Name,
            BasePrice = plan.Price,
            IncludedStorageGb = plan.IncludedStorageGb,
            UsedGb = System.Math.Round(usedGb, 2),
            OverageGb = overageGb,
            PerGbPrice = plan.PerGbOveragePrice,
            OverageAmount = overageAmount,
            Total = plan.Price + overageAmount,
        };
    }
}
