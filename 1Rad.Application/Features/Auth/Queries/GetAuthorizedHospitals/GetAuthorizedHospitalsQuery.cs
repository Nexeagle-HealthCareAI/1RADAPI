using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using System;

namespace _1Rad.Application.Features.Auth.Queries.GetAuthorizedHospitals;

public record GetAuthorizedHospitalsQuery(Guid UserId) : IRequest<GetAuthorizedHospitalsResponse>;

public class GetAuthorizedHospitalsQueryHandler : IRequestHandler<GetAuthorizedHospitalsQuery, GetAuthorizedHospitalsResponse>
{
    private readonly IApplicationDbContext _context;

    public GetAuthorizedHospitalsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<GetAuthorizedHospitalsResponse> Handle(GetAuthorizedHospitalsQuery request, CancellationToken cancellationToken)
    {
        try
        {
            var user = await _context.Users
                .Include(u => u.HospitalMappings)
                    .ThenInclude(m => m.Hospital)
                        .ThenInclude(h => h.Group)
                .Include(u => u.HospitalMappings)
                    .ThenInclude(m => m.Roles)
                .FirstOrDefaultAsync(u => u.UserId == request.UserId, cancellationToken);

            if (user == null)
            {
                return new GetAuthorizedHospitalsResponse { Success = false, Error = "User not found." };
            }

            var hospitals = user.HospitalMappings.Select(m => new AuthorizedHospitalDto
            {
                HospitalId = m.HospitalId,
                HospitalName = m.Hospital.HospitalName,
                GroupName = m.Hospital.Group?.GroupName ?? string.Empty,
                IsDefault = m.IsDefault,
                RoleNames = m.Roles.Select(r => r.RoleName).ToList()
            }).ToList();

            return new GetAuthorizedHospitalsResponse
            {
                Success = true,
                Hospitals = hospitals
            };
        }
        catch (System.Exception ex)
        {
            return new GetAuthorizedHospitalsResponse { Success = false, Error = $"Hub Discovery Failure: {ex.Message}" };
        }
    }
}
