using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Subscriptions.Commands.RejectPaymentRequest;

/// <summary>
/// Platform-admin rejection of a centre's manual payment (CMS approval screen).
/// The reason is shown to the centre on its Subscription page ("Reason: …"), and the
/// centre can submit a fresh request afterwards (only one Pending request is allowed).
/// </summary>
public record RejectPaymentRequestCommand(Guid RequestId, string Reason) : IRequest<RejectPaymentRequestResponse>;

public class RejectPaymentRequestResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class RejectPaymentRequestHandler : IRequestHandler<RejectPaymentRequestCommand, RejectPaymentRequestResponse>
{
    private readonly IApplicationDbContext _db;
    private readonly ILogger<RejectPaymentRequestHandler> _logger;

    public RejectPaymentRequestHandler(IApplicationDbContext db, ILogger<RejectPaymentRequestHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<RejectPaymentRequestResponse> Handle(RejectPaymentRequestCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return new RejectPaymentRequestResponse { Success = false, Error = "A reason is required to reject a payment." };

        var paymentRequest = await _db.SubscriptionPaymentRequests
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.RequestId == request.RequestId, cancellationToken);

        if (paymentRequest == null)
            return new RejectPaymentRequestResponse { Success = false, Error = "Payment request not found." };

        if (paymentRequest.Status != "Pending")
            return new RejectPaymentRequestResponse
            {
                Success = false,
                Error = $"Payment request is already in '{paymentRequest.Status}' status and cannot be rejected."
            };

        var reason = request.Reason.Trim();
        paymentRequest.Status = "Rejected";
        paymentRequest.ReviewNote = reason.Length > 500 ? reason[..500] : reason;
        paymentRequest.ReviewedByUserId = null; // service-key call (CMS), no 1Rad user
        paymentRequest.ReviewedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Payment request {RequestId} for HospitalId {HospitalId} rejected.", paymentRequest.RequestId, paymentRequest.HospitalId);

        return new RejectPaymentRequestResponse { Success = true };
    }
}
