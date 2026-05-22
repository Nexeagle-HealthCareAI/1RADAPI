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
    private readonly ILogger<SubmitPaymentRequestHandler> _logger;

    public SubmitPaymentRequestHandler(
        IApplicationDbContext db,
        IUserContext userContext,
        ILogger<SubmitPaymentRequestHandler> logger)
    {
        _db = db;
        _userContext = userContext;
        _logger = logger;
    }

    public async Task<SubmitPaymentRequestResponse> Handle(SubmitPaymentRequestCommand request, CancellationToken cancellationToken)
    {
        var hospitalId = _userContext.HospitalId;

        // Validate billing cycle
        if (request.BillingCycle != "Monthly" && request.BillingCycle != "Yearly")
        {
            return new SubmitPaymentRequestResponse
            {
                Success = false,
                Error = "Invalid billing cycle. Must be 'Monthly' or 'Yearly'."
            };
        }

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

        var paymentRequest = new Domain.Entities.SubscriptionPaymentRequest
        {
            HospitalId = hospitalId,
            PlanName = request.PlanName,
            BillingCycle = request.BillingCycle,
            Amount = request.Amount,
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

        _logger.LogInformation("[SubmitPaymentRequest] HospitalId={HId} submitted payment request {ReqId} for {Plan} ({Cycle})",
            hospitalId, paymentRequest.RequestId, request.PlanName, request.BillingCycle);

        return new SubmitPaymentRequestResponse
        {
            Success = true,
            RequestId = paymentRequest.RequestId
        };
    }
}
