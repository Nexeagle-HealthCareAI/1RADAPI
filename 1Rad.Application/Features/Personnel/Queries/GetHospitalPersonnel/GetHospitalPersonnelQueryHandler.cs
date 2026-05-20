using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Personnel.Queries.GetHospitalPersonnel;

public class GetHospitalPersonnelQueryHandler : IRequestHandler<GetHospitalPersonnelQuery, List<PersonnelDto>>
{
    private readonly IApplicationDbContext _context;
    private readonly ILogger<GetHospitalPersonnelQueryHandler> _logger;

    public GetHospitalPersonnelQueryHandler(IApplicationDbContext context, ILogger<GetHospitalPersonnelQueryHandler> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<List<PersonnelDto>> Handle(GetHospitalPersonnelQuery request, CancellationToken cancellationToken)
    {
        var mappings = await _context.UserHospitalMappings
            .Where(m => m.HospitalId == request.HospitalId)
            .Include(m => m.User)
            .Include(m => m.Roles)
            .ToListAsync(cancellationToken);

        // Load custom roles separately so a missing UserHospitalCustomRoles table
        // (migration not yet applied) does not crash the entire personnel query.
        var mappingIds = mappings.Select(m => m.MappingId).ToList();
        Dictionary<Guid, List<string>> customRolesByMapping = new();
        try
        {
            var customRoleRows = await _context.UserHospitalMappings
                .Where(m => mappingIds.Contains(m.MappingId))
                .SelectMany(m => m.CustomRoles.Select(cr => new { m.MappingId, cr.RoleName }))
                .ToListAsync(cancellationToken);

            customRolesByMapping = customRoleRows
                .GroupBy(x => x.MappingId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.RoleName).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CustomRoles could not be loaded for hospital {HospitalId}. The UserHospitalCustomRoles migration may not have been applied yet.", request.HospitalId);
        }

        return mappings.Select(m => new PersonnelDto(
            m.User.UserId,
            m.User.FullName,
            m.User.Email,
            m.User.Mobile,
            m.User.Password,
            m.Roles.Select(r => r.RoleName)
                .Concat(customRolesByMapping.TryGetValue(m.MappingId, out var cr) ? cr : Enumerable.Empty<string>())
                .ToList(),
            m.User.Specialization,
            m.User.Degree,
            m.User.LicenseNo,
            m.User.Status.ToString(),
            m.User.CreatedAt
        )).ToList();
    }
}
