using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Roles.Commands.DeleteCustomRole;

public class DeleteCustomRoleCommandHandler : IRequestHandler<DeleteCustomRoleCommand, (bool Success, string? Error)>
{
    private readonly IApplicationDbContext _context;

    public DeleteCustomRoleCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(bool Success, string? Error)> Handle(DeleteCustomRoleCommand request, CancellationToken cancellationToken)
    {
        var customRole = await _context.CustomRoles
            .FirstOrDefaultAsync(cr => cr.CustomRoleId == request.RoleId && cr.HospitalId == request.HospitalId, cancellationToken);

        if (customRole == null)
        {
            return (false, "Custom role not found.");
        }

        _context.CustomRoles.Remove(customRole);
        await _context.SaveChangesAsync(cancellationToken);

        return (true, null);
    }
}
