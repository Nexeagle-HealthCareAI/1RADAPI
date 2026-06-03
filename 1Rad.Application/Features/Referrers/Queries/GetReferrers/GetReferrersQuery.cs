using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.GetReferrers;

public record GetReferrersQuery(
    string? SearchQuery = null,
    DateTime? UpdatedAfter = null,
    bool IncludeDeleted = false
) : IRequest<List<ReferrerDto>>;

public class GetReferrersQueryHandler : IRequestHandler<GetReferrersQuery, List<ReferrerDto>>
{
    private readonly IApplicationDbContext _context;

    public GetReferrersQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<ReferrerDto>> Handle(GetReferrersQuery request, CancellationToken cancellationToken)
    {
        if (_context.UserContext.HospitalId == Guid.Empty)
        {
            return new List<ReferrerDto>();
        }

        var query = _context.Referrers
            .Where(r => r.HospitalId == _context.UserContext.HospitalId)
            .AsNoTracking();

        if (!request.IncludeDeleted)
        {
            query = query.Where(r => r.DeletedAt == null);
        }

        if (request.UpdatedAfter.HasValue)
        {
            var since = request.UpdatedAfter.Value;
            query = query.Where(r => r.UpdatedAt > since);
        }

        if (!string.IsNullOrEmpty(request.SearchQuery))
        {
            var search = request.SearchQuery.ToLower();
            query = query.Where(r => r.Name != null && r.Name.ToLower().Contains(search));
        }

        return await query
            .Select(r => new ReferrerDto(
                r.ReferrerId,
                r.Name ?? string.Empty,
                r.Contact ?? string.Empty,
                r.Address ?? string.Empty,
                r.UpdatedAt,
                r.DeletedAt,
                r.Email,
                r.Specialty,
                r.Degree
            ))
            .ToListAsync(cancellationToken);
    }
}
