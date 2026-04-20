using _1Rad.Application.Interfaces;
using _1Rad.Application.Features.Hospitals.Queries.GetHospitalDetails;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace _1Rad.Application.Features.Hospitals.Queries.GetGroupHospitals;

public class GetGroupHospitalsQueryHandler : IRequestHandler<GetGroupHospitalsQuery, List<HospitalDetailsDto>>
{
    private readonly IApplicationDbContext _context;
    private readonly IUserContext _userContext;

    public GetGroupHospitalsQueryHandler(IApplicationDbContext context, IUserContext userContext)
    {
        _context = context;
        _userContext = userContext;
    }

    public async Task<List<HospitalDetailsDto>> Handle(GetGroupHospitalsQuery request, CancellationToken cancellationToken)
    {
        var authorizedIds = _userContext.AuthorizedHospitalIds.ToList();
        var currentGroupId = _userContext.GroupId;

        // Fetch hospitals that the user is actively mapped to (Active Mapping Protocol)
        // Furthermore, ensure they belong to the same institutional group if a group context exists.
        var query = _context.Hospitals.AsQueryable();

        if (currentGroupId.HasValue)
        {
            query = query.Where(h => h.GroupId == currentGroupId.Value);
        }

        // Apply Active Mapping Filter
        query = query.Where(h => authorizedIds.Contains(h.HospitalId));

        return await query
            .Select(h => new HospitalDetailsDto(
                h.HospitalId,
                h.HospitalName,
                h.HospitalAddress,
                h.GSTIN,
                h.RegistrationNumber,
                h.PAN,
                h.NABHNumber,
                h.Status
            ))
            .ToListAsync(cancellationToken);
    }
}
