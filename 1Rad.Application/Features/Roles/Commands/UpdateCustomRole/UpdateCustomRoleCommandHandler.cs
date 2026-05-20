using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Roles.Commands.UpdateCustomRole;

public class UpdateCustomRoleCommandHandler : IRequestHandler<UpdateCustomRoleCommand, (bool Success, string? Error)>
{
    private readonly IApplicationDbContext _context;

    public UpdateCustomRoleCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(bool Success, string? Error)> Handle(UpdateCustomRoleCommand request, CancellationToken cancellationToken)
    {
        var customRole = await _context.CustomRoles
            .Include(cr => cr.Permissions)
            .FirstOrDefaultAsync(cr => cr.CustomRoleId == request.RoleId && cr.HospitalId == request.HospitalId, cancellationToken);

        if (customRole == null)
        {
            return (false, "Custom role not found.");
        }

        if (string.IsNullOrWhiteSpace(request.RoleName))
        {
            return (false, "Role name cannot be empty.");
        }

        var normalizedName = request.RoleName.Trim();

        var exists = await _context.CustomRoles
            .AnyAsync(cr => cr.HospitalId == request.HospitalId && 
                            cr.CustomRoleId != request.RoleId && 
                            cr.RoleName.ToLower() == normalizedName.ToLower(), cancellationToken);

        if (exists)
        {
            return (false, $"Another custom role named '{normalizedName}' already exists at this hospital.");
        }

        // Update properties
        customRole.RoleName = normalizedName;
        customRole.Description = request.Description?.Trim();

        // Update permissions relationally
        customRole.Permissions.Clear();
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

        await _context.SaveChangesAsync(cancellationToken);
        return (true, null);
    }
}
