using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Subscriptions.Queries.GetSubscriptionStatus;

public class GetSubscriptionStatusQuery : IRequest<SubscriptionStatusResponse>
{
}

public class SubscriptionStatusResponse
{
    public bool IsActive { get; set; }       // true if Status==Active or Expiring (and not locked)
    public bool IsLocked { get; set; }
    public bool IsTrial { get; set; }
    public string Status { get; set; } = string.Empty;       // Active|Expiring|Expired|Locked
    public string BillingCycle { get; set; } = string.Empty; // Trial|Monthly|Yearly
    public string? PlanName { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public int DaysRemaining { get; set; }   // Math.Max(0, (int)(EndDate - UtcNow).TotalDays)
    public bool HasPendingPaymentRequest { get; set; }
    public string? PendingRequestStatus { get; set; }  // Pending|Approved|Rejected
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

        var subscription = await _context.HospitalSubscriptions
            .Include(s => s.Plan)
            .Where(s => s.HospitalId == hospitalId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (subscription == null)
        {
            return new SubscriptionStatusResponse
            {
                IsActive = false,
                IsLocked = false,
                IsTrial = false,
                Status = "None",
                BillingCycle = string.Empty,
                DaysRemaining = 0,
                HasPendingPaymentRequest = false
            };
        }

        // Latest payment request for this hospital
        var latestPaymentRequest = await _context.SubscriptionPaymentRequests
            .Where(r => r.HospitalId == hospitalId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var daysRemaining = Math.Max(0, (int)(subscription.EndDate - now).TotalDays);
        
        // Compute effective status
        var effectiveStatus = subscription.Status;
        var effectiveIsLocked = subscription.IsLocked;

        if (!subscription.IsLocked && effectiveStatus != "None")
        {
            if (now > subscription.EndDate.AddDays(2))
            {
                effectiveStatus = "Locked";
                effectiveIsLocked = true;
            }
            else if (now > subscription.EndDate)
            {
                effectiveStatus = "Expired";
            }
            else if ((subscription.EndDate - now).TotalDays <= 3 && effectiveStatus == "Active")
            {
                effectiveStatus = "Expiring";
            }
        }

        var isActive = (effectiveStatus == "Active" || effectiveStatus == "Expiring") && !effectiveIsLocked;

        return new SubscriptionStatusResponse
        {
            IsActive = isActive,
            IsLocked = effectiveIsLocked,
            IsTrial = subscription.IsTrial,
            Status = effectiveStatus,
            BillingCycle = subscription.BillingCycle,
            PlanName = subscription.Plan?.Name ?? (subscription.IsTrial ? "Trial" : null),
            StartDate = subscription.StartDate,
            EndDate = subscription.EndDate,
            DaysRemaining = daysRemaining,
            HasPendingPaymentRequest = latestPaymentRequest?.Status == "Pending",
            PendingRequestStatus = latestPaymentRequest?.Status
        };
    }
}
