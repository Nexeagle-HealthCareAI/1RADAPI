using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using System.Net.Http;
using Microsoft.Extensions.Configuration;

namespace _1Rad.Application.Features.Subscriptions.Queries.GetPlans;

/// <summary>Lists active subscription plans (edition × cycle) for the pricing UI.</summary>
public class GetPlansQuery : IRequest<List<PlanDto>>;

public class PlanDto
{
    public Guid PlanId { get; set; }
    public string Name { get; set; } = string.Empty;        // Monthly | Yearly | PAYG | Custom
    public string Edition { get; set; } = string.Empty;     // RIS | RIS+PACS | PACS
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

public class GetPlansQueryHandler : IRequestHandler<GetPlansQuery, List<PlanDto>>
{
    private static readonly HttpClient _httpClient = new HttpClient();
    private readonly IConfiguration _configuration;

    public GetPlansQueryHandler(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<List<PlanDto>> Handle(GetPlansQuery request, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = _configuration["CmsApiBaseUrl"]?.TrimEnd('/') ?? "http://localhost:5176";
            // Fetch from CMSAPI
            var response = await _httpClient.GetAsync($"{baseUrl}/api/v1/SubscriptionPlans", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var cmsPlans = await response.Content.ReadFromJsonAsync<List<CmsPlanDto>>(cancellationToken: cancellationToken);
                if (cmsPlans != null)
                {
                    return cmsPlans
                        .Where(p => p.IsActive && p.ApplicationName == "1Rad")
                        .Select(p => new PlanDto
                        {
                            PlanId = p.PlanId,
                            Name = p.Name,
                            Price = p.BasePrice,
                            DiscountPrice = p.DiscountPrice,
                            Edition = "1Rad Premium", // Default to premium for simplicity
                            DurationInDays = p.BillingCycle.Equals("Yearly", StringComparison.OrdinalIgnoreCase) ? 365 : 30,
                            BillingMode = "Subscription"
                        })
                        .OrderBy(p => p.Price)
                        .ToList();
                }
            }
        }
        catch (Exception ex)
        {
            // Log error or fallback
            Console.WriteLine($"Error fetching plans from CMSAPI: {ex.Message}");
        }

        return new List<PlanDto>(); // Return empty on failure
    }

    private class CmsPlanDto
    {
        public Guid PlanId { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal BasePrice { get; set; }
        public decimal DiscountPrice { get; set; }
        public string BillingCycle { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }
}
