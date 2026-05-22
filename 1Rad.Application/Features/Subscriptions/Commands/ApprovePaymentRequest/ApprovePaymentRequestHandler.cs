using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Subscriptions.Commands.ApprovePaymentRequest;

public class ApprovePaymentRequestHandler : IRequestHandler<ApprovePaymentRequestCommand, ApprovePaymentRequestResponse>
{
    private readonly IApplicationDbContext _db;
    private readonly IUserContext _userContext;
    private readonly ILogger<ApprovePaymentRequestHandler> _logger;

    public ApprovePaymentRequestHandler(
        IApplicationDbContext db,
        IUserContext userContext,
        ILogger<ApprovePaymentRequestHandler> logger)
    {
        _db = db;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<ApprovePaymentRequestResponse> Handle(ApprovePaymentRequestCommand request, CancellationToken cancellationToken)
    {
        // 1. Find the payment request — IgnoreQueryFilters because this is an admin operation
        var paymentRequest = await _db.SubscriptionPaymentRequests
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.RequestId == request.RequestId, cancellationToken);

        if (paymentRequest == null)
        {
            return new ApprovePaymentRequestResponse { Success = false, Error = "Payment request not found." };
        }

        if (paymentRequest.Status != "Pending")
        {
            return new ApprovePaymentRequestResponse
            {
                Success = false,
                Error = $"Payment request is already in '{paymentRequest.Status}' status and cannot be approved."
            };
        }

        // 2. Find the plan by billing cycle name
        var plan = await _db.SubscriptionPlans
            .FirstOrDefaultAsync(p => p.Name == paymentRequest.BillingCycle, cancellationToken);

        if (plan == null)
        {
            return new ApprovePaymentRequestResponse
            {
                Success = false,
                Error = $"Subscription plan '{paymentRequest.BillingCycle}' not found in the system."
            };
        }

        var now = DateTime.UtcNow;
        var hospitalId = paymentRequest.HospitalId;

        // 3. Supersede any existing Active/Expiring/Expired subscriptions for this hospital
        var existingSubscriptions = await _db.HospitalSubscriptions
            .IgnoreQueryFilters()
            .Where(s => s.HospitalId == hospitalId &&
                        (s.Status == "Active" || s.Status == "Expiring" || s.Status == "Expired" || s.Status == "Locked"))
            .ToListAsync(cancellationToken);

        foreach (var existing in existingSubscriptions)
        {
            existing.Status = "Superseded";
            existing.IsLocked = false;
        }

        // 4. Create new active subscription
        var endDate = paymentRequest.BillingCycle == "Yearly"
            ? now.AddDays(365)
            : now.AddDays(30);

        var newSubscription = new HospitalSubscription
        {
            HospitalId = hospitalId,
            PlanId = plan.PlanId,
            BillingCycle = paymentRequest.BillingCycle,
            IsTrial = false,
            StartDate = now,
            EndDate = endDate,
            Status = "Active",
            IsLocked = false,
            ActivatedByUserId = _userContext.UserId,
            ActivatedAt = now,
            CreatedAt = now
        };

        _db.HospitalSubscriptions.Add(newSubscription);

        // 5. Approve the payment request
        paymentRequest.Status = "Approved";
        paymentRequest.ReviewNote = request.ReviewNote;
        paymentRequest.ReviewedByUserId = _userContext.UserId;
        paymentRequest.ReviewedAt = now;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "[ApprovePaymentRequest] RequestId={ReqId} approved. HospitalId={HId} activated {Plan} plan until {EndDate}.",
            request.RequestId, hospitalId, paymentRequest.BillingCycle, endDate);

        return new ApprovePaymentRequestResponse { Success = true };
    }
}
