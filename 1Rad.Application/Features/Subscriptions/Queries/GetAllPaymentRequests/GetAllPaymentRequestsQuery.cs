using MediatR;

namespace _1Rad.Application.Features.Subscriptions.Queries.GetAllPaymentRequests;

public record GetAllPaymentRequestsQuery() : IRequest<List<PaymentRequestDto>>;

public record PaymentRequestDto(
    Guid RequestId,
    Guid HospitalId,
    string HospitalName,
    string PlanName,
    string BillingCycle,
    string Status,
    string? ReviewNote,
    DateTime CreatedAt,
    string PaymentMode,
    decimal Amount,
    // Edition being purchased + the metered storage overage in the amount, so
    // the admin can verify what they're approving.
    string? Modules,
    int StorageOverageGb,
    decimal StorageOverageAmount
);
