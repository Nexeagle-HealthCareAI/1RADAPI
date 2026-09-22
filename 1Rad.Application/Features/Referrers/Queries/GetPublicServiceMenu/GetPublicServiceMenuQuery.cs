using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.GetPublicServiceMenu;

/// <summary>Modality + service name only - never Amount or ReferralCutValue. This is read by an
/// outside referring doctor from their portal link; pricing and commission rates are the centre's
/// internal business, not something a public, token-gated endpoint should ever expose.</summary>
public record PublicServiceDto(string Modality, string ServiceName);

/// <summary>The centre's service menu, for the "what would you like booked" picker on the doctor
/// portal's booking form. Anonymous (capability-link) caller - HospitalId comes from the referrer,
/// not from a user context.</summary>
public record GetPublicServiceMenuQuery(Guid ReferrerId) : IRequest<List<PublicServiceDto>>;

public class GetPublicServiceMenuQueryHandler : IRequestHandler<GetPublicServiceMenuQuery, List<PublicServiceDto>>
{
    private readonly IApplicationDbContext _context;

    public GetPublicServiceMenuQueryHandler(IApplicationDbContext context) => _context = context;

    public async Task<List<PublicServiceDto>> Handle(GetPublicServiceMenuQuery request, CancellationToken ct)
    {
        var referrer = await _context.Referrers.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.ReferrerId == request.ReferrerId && r.DeletedAt == null, ct);
        if (referrer == null) return new List<PublicServiceDto>();

        return await _context.ServiceCharges.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.HospitalId == referrer.HospitalId)
            .OrderBy(s => s.Modality).ThenBy(s => s.ServiceName)
            .Select(s => new PublicServiceDto(s.Modality, s.ServiceName))
            .Distinct()
            .ToListAsync(ct);
    }
}
