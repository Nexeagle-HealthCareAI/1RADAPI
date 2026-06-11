using _1Rad.Application.Features.Subscriptions.Queries.GetBillingEstimate;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Subscriptions.Commands.SubmitPaymentRequest;

public class SubmitPaymentRequestHandler : IRequestHandler<SubmitPaymentRequestCommand, SubmitPaymentRequestResponse>
{
    private readonly IApplicationDbContext _db;
    private readonly IUserContext _userContext;
    private readonly IStorageMeteringService _storage;
    private readonly ILogger<SubmitPaymentRequestHandler> _logger;

    public SubmitPaymentRequestHandler(
        IApplicationDbContext db,
        IUserContext userContext,
        IStorageMeteringService storage,
        ILogger<SubmitPaymentRequestHandler> logger)
    {
        _db = db;
        _userContext = userContext;
        _storage = storage;
        _logger = logger;
    }

    public async Task<SubmitPaymentRequestResponse> Handle(SubmitPaymentRequestCommand request, CancellationToken cancellationToken)
    {
        var hospitalId = _userContext.HospitalId;

        // Check for existing pending request
        var hasPending = await _db.SubscriptionPaymentRequests
            .AnyAsync(r => r.HospitalId == hospitalId && r.Status == "Pending", cancellationToken);

        if (hasPending)
        {
            return new SubmitPaymentRequestResponse
            {
                Success = false,
                Error = "A payment request is already under review."
            };
        }

        // Edition-aware path: derive cycle / modules / overage / authoritative
        // Amount from the chosen plan + current usage (don't trust the client).
        var planName = request.PlanName;
        var billingCycle = request.BillingCycle;
        var amount = request.Amount;
        string? modules = null;
        var overageGb = 0;
        var overageAmount = 0m;
        Guid? planId = null;

        if (request.PlanId is Guid pid && pid != Guid.Empty)
        {
            var plan = await _db.SubscriptionPlans
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PlanId == pid && p.IsActive, cancellationToken);
            if (plan == null)
                return new SubmitPaymentRequestResponse { Success = false, Error = "Selected plan not found." };

            var est = BillingMath.Estimate(plan, await _storage.GetUsageAsync(hospitalId, cancellationToken));
            planId = plan.PlanId;
            planName = plan.Name;
            billingCycle = plan.Name;           // cycle is the plan Name (Monthly/Yearly)
            amount = est.Total;                 // server-computed amount due
            modules = plan.Modules;
            overageGb = est.OverageGb;
            overageAmount = est.OverageAmount;
        }
        else if (billingCycle != "Monthly" && billingCycle != "Yearly")
        {
            return new SubmitPaymentRequestResponse
            {
                Success = false,
                Error = "Invalid billing cycle. Must be 'Monthly' or 'Yearly'."
            };
        }

        var paymentRequest = new Domain.Entities.SubscriptionPaymentRequest
        {
            HospitalId = hospitalId,
            PlanId = planId,
            PlanName = planName,
            BillingCycle = billingCycle,
            Amount = amount,
            Modules = modules,
            StorageOverageGb = overageGb,
            StorageOverageAmount = overageAmount,
            PayerName = request.PayerName,
            PayerContact = request.PayerContact,
            TransactionReference = request.TransactionReference,
            PaymentMode = request.PaymentMode,
            PaidAt = request.PaidAt,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };

        _db.SubscriptionPaymentRequests.Add(paymentRequest);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[SubmitPaymentRequest] HospitalId={HId} submitted payment request {ReqId} for {Plan} ({Cycle}) amount={Amount}",
            hospitalId, paymentRequest.RequestId, planName, billingCycle, amount);

        return new SubmitPaymentRequestResponse
        {
            Success = true,
            RequestId = paymentRequest.RequestId
        };
    }
}
