using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Subscriptions.Queries.GetSubscriptionTransactions;

public class GetSubscriptionTransactionsQuery : IRequest<List<TransactionDto>>
{
}

public class TransactionDto
{
    public Guid RequestId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public string BillingCycle { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string TransactionReference { get; set; } = string.Empty;
    public string PaymentMode { get; set; } = string.Empty;
    public DateTime PaidAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? ReviewNote { get; set; }
}

public class GetSubscriptionTransactionsQueryHandler : IRequestHandler<GetSubscriptionTransactionsQuery, List<TransactionDto>>
{
    private readonly IApplicationDbContext _context;
    private readonly IUserContext _userContext;

    public GetSubscriptionTransactionsQueryHandler(IApplicationDbContext context, IUserContext userContext)
    {
        _context = context;
        _userContext = userContext;
    }

    public async Task<List<TransactionDto>> Handle(GetSubscriptionTransactionsQuery request, CancellationToken cancellationToken)
    {
        var hospitalId = _userContext.HospitalId;

        var transactions = await _context.SubscriptionPaymentRequests
            .Where(x => x.HospitalId == hospitalId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new TransactionDto
            {
                RequestId = x.RequestId,
                PlanName = x.PlanName,
                BillingCycle = x.BillingCycle,
                Amount = x.Amount,
                TransactionReference = x.TransactionReference,
                PaymentMode = x.PaymentMode,
                PaidAt = x.PaidAt,
                Status = x.Status,
                CreatedAt = x.CreatedAt,
                ReviewNote = x.ReviewNote
            })
            .ToListAsync(cancellationToken);

        return transactions;
    }
}
