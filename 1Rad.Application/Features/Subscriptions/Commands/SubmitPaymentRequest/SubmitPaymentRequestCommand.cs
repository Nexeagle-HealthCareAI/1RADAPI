using MediatR;

namespace _1Rad.Application.Features.Subscriptions.Commands.SubmitPaymentRequest;

public record SubmitPaymentRequestCommand(
    string PlanName,
    string BillingCycle,
    decimal Amount,
    string PayerName,
    string PayerContact,
    string TransactionReference,
    string PaymentMode,
    DateTime PaidAt,
    // Preferred: the chosen plan (edition × cycle). When set, the server derives
    // the cycle, modules, storage overage and the authoritative Amount from the
    // plan + current usage (the client Amount is ignored). Null = legacy path.
    Guid? PlanId = null
) : IRequest<SubmitPaymentRequestResponse>;

public class SubmitPaymentRequestResponse
{
    public bool Success { get; set; }
    public Guid? RequestId { get; set; }
    public string? Error { get; set; }
}
