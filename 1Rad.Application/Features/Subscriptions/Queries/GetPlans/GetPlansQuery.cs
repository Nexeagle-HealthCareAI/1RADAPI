using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Subscriptions.Queries.GetPlans;

/// <summary>Lists active subscription plans (edition × tier × cycle) for the pricing UI.</summary>
public class GetPlansQuery : IRequest<List<PlanDto>>;

public class PlanDto
{
    public Guid PlanId { get; set; }
    public string Name { get; set; } = string.Empty;        // Monthly | Yearly | PAYG | Custom
    public string Edition { get; set; } = string.Empty;     // RIS | RIS+PACS | PACS
    public string Tier { get; set; } = string.Empty;        // Starter | Growth | Clinic | PAYG | Chain
    public string Modules { get; set; } = string.Empty;     // RIS / RIS,PACS / PACS
    public decimal Price { get; set; }
    public decimal DiscountPrice { get; set; }
    public int DurationInDays { get; set; }
    public decimal DiscountPercentage { get; set; }
    public int? IncludedStorageGb { get; set; }
    public decimal PerGbOveragePrice { get; set; }
    public string BillingMode { get; set; } = string.Empty; // Subscription | PerStudy
    public decimal PerStudyPrice { get; set; }
    public int? MaxUsers { get; set; }
    public int? MaxSites { get; set; }
    public bool IsCustom { get; set; }
}

// Gap analysis P0-1: this used to proxy the CMS catalogue (different PlanIds, Edition
// hard-coded to "1Rad Premium", no tier/modules/caps), while payment-request, estimate
// and approval all priced from the local SubscriptionPlans table — so a plan picked in
// the UI was "not found" at payment time. The local table (seeded by DB script 72) is
// now the single source of truth for 1Rad plans.
public class GetPlansQueryHandler : IRequestHandler<GetPlansQuery, List<PlanDto>>
{
    private readonly IApplicationDbContext _context;

    public GetPlansQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<PlanDto>> Handle(GetPlansQuery request, CancellationToken cancellationToken)
    {
        return await _context.SubscriptionPlans
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Edition)
            .ThenBy(p => p.IsCustom)
            .ThenBy(p => p.BillingMode)
            .ThenBy(p => p.Price)
            .Select(p => new PlanDto
            {
                PlanId = p.PlanId,
                Name = p.Name,
                Edition = p.Edition,
                Tier = p.Tier,
                Modules = p.Modules,
                Price = p.Price,
                DiscountPrice = 0,
                DurationInDays = p.DurationInDays,
                DiscountPercentage = p.DiscountPercentage,
                IncludedStorageGb = p.IncludedStorageGb,
                PerGbOveragePrice = p.PerGbOveragePrice,
                BillingMode = p.BillingMode,
                PerStudyPrice = p.PerStudyPrice,
                MaxUsers = p.MaxUsers,
                MaxSites = p.MaxSites,
                IsCustom = p.IsCustom,
            })
            .ToListAsync(cancellationToken);
    }
}
