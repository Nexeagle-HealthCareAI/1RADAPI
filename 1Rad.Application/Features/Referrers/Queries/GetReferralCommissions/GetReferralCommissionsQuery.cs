using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.GetReferralCommissions;

public record GetReferralCommissionsQuery(
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    Guid? ReferrerId = null
) : IRequest<List<ReferralCommissionDto>>;

public record ReferralCommissionDto(
    Guid Id,
    Guid ReferrerId,
    string? ReferrerName,
    string? Modality,
    decimal Amount,
    decimal AccumulatedTotal,
    DateTime TransactionDate,
    string Status,
    string? ReferenceNumber,
    string? Remarks,
    string? PatientName
);


public class GetReferralCommissionsQueryHandler : IRequestHandler<GetReferralCommissionsQuery, List<ReferralCommissionDto>>
{
    private readonly IApplicationDbContext _context;

    public GetReferralCommissionsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<ReferralCommissionDto>> Handle(GetReferralCommissionsQuery request, CancellationToken cancellationToken)
    {
        var query = _context.ReferralCommissions
            .AsNoTracking()
            .AsQueryable();

        if (request.ReferrerId.HasValue)
            query = query.Where(c => c.ReferrerId == request.ReferrerId.Value);

        if (request.StartDate.HasValue)
            query = query.Where(c => c.TransactionDate >= request.StartDate.Value);

        if (request.EndDate.HasValue)
            query = query.Where(c => c.TransactionDate <= request.EndDate.Value);

        return await query
            .OrderByDescending(c => c.TransactionDate)
            .Select(c => new ReferralCommissionDto(
                c.Id,
                c.ReferrerId,
                c.ReferrerName ?? "Unknown Referrer",
                c.Modality ?? "Unknown",
                c.CommissionAmount,
                c.AccumulatedTotal,
                c.TransactionDate,
                c.Status ?? "UNPAID",
                c.ReferenceNumber,
                c.Remarks,
                c.PatientName ?? "Unknown Patient"
            ))
            .ToListAsync(cancellationToken);
    }
}
