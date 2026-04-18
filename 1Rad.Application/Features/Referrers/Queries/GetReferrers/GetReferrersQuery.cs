using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.GetReferrers;

public record GetReferrersQuery(string? SearchQuery = null) : IRequest<List<ReferrerDto>>;

public class GetReferrersQueryHandler : IRequestHandler<GetReferrersQuery, List<ReferrerDto>>
{
    private readonly IApplicationDbContext _context;

    public GetReferrersQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<ReferrerDto>> Handle(GetReferrersQuery request, CancellationToken cancellationToken)
    {
        var query = _context.Referrers.AsNoTracking();

        if (!string.IsNullOrEmpty(request.SearchQuery))
        {
            var search = request.SearchQuery.ToLower();
            query = query.Where(r => r.Name.ToLower().Contains(search));
        }

        return await query
            .Select(r => new ReferrerDto(r.ReferrerId, r.Name, r.Contact, r.Address))
            .ToListAsync(cancellationToken);
    }
}
