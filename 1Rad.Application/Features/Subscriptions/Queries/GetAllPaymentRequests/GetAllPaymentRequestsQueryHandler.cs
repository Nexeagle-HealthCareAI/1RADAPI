using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Subscriptions.Queries.GetAllPaymentRequests;

public class GetAllPaymentRequestsQueryHandler : IRequestHandler<GetAllPaymentRequestsQuery, List<PaymentRequestDto>>
{
    private readonly IApplicationDbContext _db;

    public GetAllPaymentRequestsQueryHandler(IApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<List<PaymentRequestDto>> Handle(GetAllPaymentRequestsQuery request, CancellationToken cancellationToken)
    {
        // We use IgnoreQueryFilters() so Nexeagle Admins can see all requests regardless of tenant ID.
        var requests = await _db.SubscriptionPaymentRequests
            .IgnoreQueryFilters()
            .Include(pr => pr.Hospital)
            .OrderByDescending(pr => pr.CreatedAt)
            .Select(pr => new PaymentRequestDto(
                pr.RequestId,
                pr.HospitalId,
                pr.Hospital != null ? pr.Hospital.HospitalName : "Unknown Hospital",
                pr.PlanName,
                pr.BillingCycle,
                pr.Status,
                pr.ReviewNote,
                pr.CreatedAt,
                pr.PaymentMode,
                pr.Amount
            ))
            .ToListAsync(cancellationToken);

        return requests;
    }
}
