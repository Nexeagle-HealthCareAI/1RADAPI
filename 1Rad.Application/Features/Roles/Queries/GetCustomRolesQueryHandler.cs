using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Roles.Queries;

public class GetCustomRolesQueryHandler : IRequestHandler<GetCustomRolesQuery, List<CustomRoleDto>>
{
    private readonly IApplicationDbContext _context;

    public GetCustomRolesQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<CustomRoleDto>> Handle(GetCustomRolesQuery request, CancellationToken cancellationToken)
    {
        // Global query filter automatically restricts CustomRoles to request.HospitalId if using IHospitalContext
        // But we explicitly filter by HospitalId for query safety as well.
        return await _context.CustomRoles
            .Where(cr => cr.HospitalId == request.HospitalId)
            .Include(cr => cr.Permissions)
            .Select(cr => new CustomRoleDto(
                cr.CustomRoleId,
                cr.RoleName,
                cr.Description,
                cr.Permissions.Select(p => p.RoutePath).ToList()
            ))
            .ToListAsync(cancellationToken);
    }
}
