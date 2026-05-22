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
    DateTime PaidAt
) : IRequest<SubmitPaymentRequestResponse>;

public class SubmitPaymentRequestResponse
{
    public bool Success { get; set; }
    public Guid? RequestId { get; set; }
    public string? Error { get; set; }
}
