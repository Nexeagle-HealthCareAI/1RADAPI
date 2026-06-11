using System.Linq;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Constants;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Subscriptions.Commands.ApprovePaymentRequest;

public class ApprovePaymentRequestHandler : IRequestHandler<ApprovePaymentRequestCommand, ApprovePaymentRequestResponse>
{
    private readonly IApplicationDbContext _db;
    private readonly IUserContext _userContext;
    private readonly IModuleEntitlementService _modules;
    private readonly IStorageMeteringService _storage;
    private readonly ILogger<ApprovePaymentRequestHandler> _logger;

    public ApprovePaymentRequestHandler(
        IApplicationDbContext db,
        IUserContext userContext,
        IModuleEntitlementService modules,
        IStorageMeteringService storage,
        ILogger<ApprovePaymentRequestHandler> logger)
    {
        _db = db;
        _userContext = userContext;
        _modules = modules;
        _storage = storage;
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

        // 2. Resolve the plan. Prefer the exact PlanId the center chose (the
        //    edition); fall back to the RIS+PACS plan for the cycle for legacy
        //    requests that predate editions (Name alone is now ambiguous — 3
        //    editions share each cycle).
        SubscriptionPlan? plan = null;
        if (paymentRequest.PlanId is Guid pid && pid != Guid.Empty)
            plan = await _db.SubscriptionPlans.FirstOrDefaultAsync(p => p.PlanId == pid, cancellationToken);
        plan ??= await _db.SubscriptionPlans
            .FirstOrDefaultAsync(p => p.Name == paymentRequest.BillingCycle && p.Edition == "RIS+PACS", cancellationToken);
        plan ??= await _db.SubscriptionPlans
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

        // The edition being activated. Prefer what was recorded on the request;
        // else the plan's modules.
        var newModules = !string.IsNullOrWhiteSpace(paymentRequest.Modules) ? paymentRequest.Modules! : plan.Modules;
        var newModuleSet = ModuleConstants.Parse(newModules);

        // 3. Supersede any existing Active/Expiring/Expired subscriptions for this hospital
        var existingSubscriptions = await _db.HospitalSubscriptions
            .IgnoreQueryFilters()
            .Where(s => s.HospitalId == hospitalId &&
                        (s.Status == "Active" || s.Status == "Expiring" || s.Status == "Expired" || s.Status == "Locked"))
            .ToListAsync(cancellationToken);

        // Did the center have PACS before this approval? Drives the downgrade
        // grace clock when the new edition drops PACS.
        var hadPacs = existingSubscriptions
            .Any(s => ModuleConstants.Parse(s.Modules).Contains(ModuleConstants.Pacs));

        foreach (var existing in existingSubscriptions)
        {
            existing.Status = "Superseded";
            existing.IsLocked = false;
        }

        // 4. Create new active subscription with the purchased edition + storage.
        var endDate = plan.DurationInDays > 0 ? now.AddDays(plan.DurationInDays)
            : (paymentRequest.BillingCycle == "Yearly" ? now.AddDays(365) : now.AddDays(30));

        var nowHasPacs = newModuleSet.Contains(ModuleConstants.Pacs);

        var newSubscription = new HospitalSubscription
        {
            HospitalId = hospitalId,
            PlanId = plan.PlanId,
            BillingCycle = paymentRequest.BillingCycle,
            Modules = string.Join(",", newModuleSet.OrderBy(m => m)),
            IncludedStorageGb = plan.IncludedStorageGb,
            // Tier billing mode + caps, copied so enforcement + PAYG billing read one row.
            BillingMode = plan.BillingMode,
            PerStudyPrice = plan.PerStudyPrice,
            MaxUsers = plan.MaxUsers,
            MaxSites = plan.MaxSites,
            // Dropping PACS starts the read-only grace; otherwise no grace.
            PacsRemovedAt = (!nowHasPacs && hadPacs) ? now : (DateTime?)null,
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

        // SKU/storage changed — drop the cached entitlement + usage so the new
        // modules take effect immediately.
        _modules.Invalidate(hospitalId);
        _storage.Invalidate(hospitalId);

        _logger.LogInformation(
            "[ApprovePaymentRequest] RequestId={ReqId} approved. HospitalId={HId} activated {Plan} plan until {EndDate}.",
            request.RequestId, hospitalId, paymentRequest.BillingCycle, endDate);

        return new ApprovePaymentRequestResponse { Success = true };
    }
}
