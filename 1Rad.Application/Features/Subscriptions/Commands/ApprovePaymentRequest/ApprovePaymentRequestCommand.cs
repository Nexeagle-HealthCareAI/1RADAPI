using MediatR;

namespace _1Rad.Application.Features.Subscriptions.Commands.ApprovePaymentRequest;

public record ApprovePaymentRequestCommand(Guid RequestId, string? ReviewNote) : IRequest<ApprovePaymentRequestResponse>;

public class ApprovePaymentRequestResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}
