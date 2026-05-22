using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace _1Rad.Application.Features.Hospitals.Queries.GetGroupHospitals;

public class GetGroupHospitalsQueryHandler : IRequestHandler<GetGroupHospitalsQuery, List<GroupHospitalDto>>
{
    private readonly IApplicationDbContext _context;
    private readonly IUserContext _userContext;

    public GetGroupHospitalsQueryHandler(IApplicationDbContext context, IUserContext userContext)
    {
        _context = context;
        _userContext = userContext;
    }

    public async Task<List<GroupHospitalDto>> Handle(GetGroupHospitalsQuery request, CancellationToken cancellationToken)
    {
        var authorizedIds = _userContext.AuthorizedHospitalIds.ToList();
        var currentGroupId = _userContext.GroupId;

        var query = _context.Hospitals.AsQueryable();

        if (currentGroupId.HasValue)
        {
            query = query.Where(h => h.GroupId == currentGroupId.Value);
        }

        query = query.Where(h => authorizedIds.Contains(h.HospitalId));

        return await query
            .Select(h => new GroupHospitalDto(
                h.HospitalId,
                h.HospitalName ?? "Unknown",
                h.HospitalAddress ?? "Unknown",
                h.GSTIN,
                h.RegistrationNumber,
                h.PAN,
                h.NABHNumber,
                h.Status,
                h.IsAutoBillingEnabled
            ))
            .ToListAsync(cancellationToken);
    }
}
