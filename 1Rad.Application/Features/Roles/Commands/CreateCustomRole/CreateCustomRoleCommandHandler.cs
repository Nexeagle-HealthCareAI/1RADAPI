using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
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

        foreach (var route in request.Permissions.Distinct())
        {
            if (!string.IsNullOrWhiteSpace(route))
            {
                customRole.Permissions.Add(new CustomRolePermission
                {
                    RoutePath = route.Trim()
                });
            }
        }

        _context.CustomRoles.Add(customRole);
        await _context.SaveChangesAsync(cancellationToken);

        return (customRole.CustomRoleId, null);
    }
}
