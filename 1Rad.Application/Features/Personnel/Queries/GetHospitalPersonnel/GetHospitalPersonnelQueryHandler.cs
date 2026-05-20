using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Personnel.Queries.GetHospitalPersonnel;

public class GetHospitalPersonnelQueryHandler : IRequestHandler<GetHospitalPersonnelQuery, List<PersonnelDto>>
{
    private readonly IApplicationDbContext _context;

    public GetHospitalPersonnelQueryHandler(IApplicationDbContext _context)
    {
        this._context = _context;
    }

    public async Task<List<PersonnelDto>> Handle(GetHospitalPersonnelQuery request, CancellationToken cancellationToken)
    {
        return await _context.UserHospitalMappings
            .Where(m => m.HospitalId == request.HospitalId)
            .Include(m => m.User)
            .Include(m => m.Roles)
            .Include(m => m.CustomRoles)
            .Select(m => new PersonnelDto(
                m.User.UserId,
                m.User.FullName,
                m.User.Email,
                m.User.Mobile,
                m.User.Password,
                m.Roles.Select(r => r.RoleName).Concat(m.CustomRoles.Select(cr => cr.RoleName)).ToList(),
                m.User.Specialization,
                m.User.Degree,
                m.User.LicenseNo,
                m.User.Status.ToString(),
                m.User.CreatedAt
            ))
            .ToListAsync(cancellationToken);
    }
}
