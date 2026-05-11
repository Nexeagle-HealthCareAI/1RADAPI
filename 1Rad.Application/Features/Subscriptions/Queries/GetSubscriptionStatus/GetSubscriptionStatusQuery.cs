using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Subscriptions.Queries.GetSubscriptionStatus;

public class GetSubscriptionStatusQuery : IRequest<SubscriptionStatusResponse>
{
}

public class SubscriptionStatusResponse
{
    public bool IsActive { get; set; }
    public bool IsTrial { get; set; }
    public DateTime EndDate { get; set; }
    public int DaysRemaining { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? PlanName { get; set; }
    public int DoctorCount { get; set; }
    public decimal AdditionalDoctorSurcharge { get; set; }
    public decimal TotalBasePrice { get; set; }
}

public class GetSubscriptionStatusQueryHandler : IRequestHandler<GetSubscriptionStatusQuery, SubscriptionStatusResponse>
{
    private readonly IApplicationDbContext _context;
    private readonly IUserContext _userContext;

    public GetSubscriptionStatusQueryHandler(IApplicationDbContext context, IUserContext userContext)
    {
        _context = context;
        _userContext = userContext;
    }

    public async Task<SubscriptionStatusResponse> Handle(GetSubscriptionStatusQuery request, CancellationToken cancellationToken)
    {
        var hospitalId = _userContext.HospitalId;
        
        var currentSubscription = await _context.HospitalSubscriptions
            .Include(s => s.Plan)
            .Where(s => s.HospitalId == hospitalId)
            .OrderByDescending(s => s.EndDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (currentSubscription == null)
        {
            return new SubscriptionStatusResponse { IsActive = false, Status = "None" };
        }

        var daysRemaining = (currentSubscription.EndDate - DateTime.UtcNow).Days;

        // Count doctors in this hospital
        var doctorCount = await _context.UserHospitalMappings
            .Where(m => m.HospitalId == hospitalId)
            .Where(m => m.Roles.Any(r => r.RoleName == "Doctor" || r.RoleName == "AdminDoctor"))
            .CountAsync(cancellationToken);

        decimal surcharge = 0;
        if (doctorCount > 1 && currentSubscription.Plan != null)
        {
            surcharge = (doctorCount - 1) * currentSubscription.Plan.PerAdditionalDoctorPrice;
        }

        return new SubscriptionStatusResponse
        {
            IsActive = currentSubscription.Status == "Active" && currentSubscription.EndDate > DateTime.UtcNow,
            IsTrial = currentSubscription.IsTrial,
            EndDate = currentSubscription.EndDate,
            DaysRemaining = daysRemaining > 0 ? daysRemaining : 0,
            Status = currentSubscription.Status,
            PlanName = currentSubscription.Plan?.Name ?? (currentSubscription.IsTrial ? "Trial" : "None"),
            DoctorCount = doctorCount,
            AdditionalDoctorSurcharge = surcharge,
            TotalBasePrice = currentSubscription.Plan?.Price ?? 0
        };
    }
}
