using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Subscriptions.Queries.GetPlans;

/// <summary>Lists active subscription plans (edition × cycle) for the pricing UI.</summary>
public class GetPlansQuery : IRequest<List<PlanDto>>;

public class PlanDto
{
    public Guid PlanId { get; set; }
    public string Name { get; set; } = string.Empty;        // Monthly | Yearly
    public string Edition { get; set; } = string.Empty;     // RIS | RIS+PACS | PACS
    public string Modules { get; set; } = string.Empty;     // RIS / RIS,PACS / PACS
    public decimal Price { get; set; }
    public int DurationInDays { get; set; }
    public decimal DiscountPercentage { get; set; }
    public int? IncludedStorageGb { get; set; }
    public decimal PerGbOveragePrice { get; set; }
}

public class GetPlansQueryHandler : IRequestHandler<GetPlansQuery, List<PlanDto>>
{
    private readonly IApplicationDbContext _context;

    public GetPlansQueryHandler(IApplicationDbContext context) => _context = context;

    public async Task<List<PlanDto>> Handle(GetPlansQuery request, CancellationToken cancellationToken)
    {
        return await _context.SubscriptionPlans
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Edition).ThenBy(p => p.DurationInDays)
            .Select(p => new PlanDto
            {
                PlanId = p.PlanId,
                Name = p.Name,
                Edition = p.Edition,
                Modules = p.Modules,
                Price = p.Price,
                DurationInDays = p.DurationInDays,
                DiscountPercentage = p.DiscountPercentage,
                IncludedStorageGb = p.IncludedStorageGb,
                PerGbOveragePrice = p.PerGbOveragePrice,
            })
            .ToListAsync(cancellationToken);
    }
}
