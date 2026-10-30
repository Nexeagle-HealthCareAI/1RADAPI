using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Constants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Roles.Commands.CreateCustomRole;

public class CreateCustomRoleCommandHandler : IRequestHandler<CreateCustomRoleCommand, (Guid RoleId, string? Error)>
{
    private readonly IApplicationDbContext _context;

    public CreateCustomRoleCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(Guid RoleId, string? Error)> Handle(CreateCustomRoleCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RoleName))
        {
            return (Guid.Empty, "Role name cannot be empty.");
        }

        var normalizedName = request.RoleName.Trim();

        var exists = await _context.CustomRoles
            .AnyAsync(cr => cr.HospitalId == request.HospitalId && cr.RoleName.ToLower() == normalizedName.ToLower(), cancellationToken);

        if (exists)
        {
            return (Guid.Empty, $"A custom role named '{normalizedName}' already exists at this hospital.");
        }

        var customRole = new CustomRole
        {
            HospitalId = request.HospitalId,
            RoleName = normalizedName,
            Description = request.Description?.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        // Only page routes the API knows how to translate may be saved (gap analysis
        // P0-5 / P1-2): an arbitrary string such as "api" used to grant every role.
        var unknownRoutes = (request.Permissions ?? new List<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r) && !CustomRoleRouteMap.IsKnownPage(r))
            .ToList();
        if (unknownRoutes.Count > 0)
        {
            return (Guid.Empty, $"Unknown page permission(s): {string.Join(", ", unknownRoutes)}.");
        }

        foreach (var route in request.Permissions.Where(r => !string.IsNullOrWhiteSpace(r)).Select(CustomRoleRouteMap.Normalise).Distinct())
        {
            if (!string.IsNullOrWhiteSpace(route))
            {
                customRole.Permissions.Add(new CustomRolePermission
                {
                    RoutePath = CustomRoleRouteMap.Normalise(route)
                });
            }
        }

        _context.CustomRoles.Add(customRole);
        await _context.SaveChangesAsync(cancellationToken);

        return (customRole.CustomRoleId, null);
    }
}
