using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Personnel.Commands.UpdateStaff;

public class UpdateStaffCommandHandler : IRequestHandler<UpdateStaffCommand, (bool Success, string? Error)>
{
    private readonly IApplicationDbContext _context;
    private readonly IPasswordHasher _passwordHasher;

    public UpdateStaffCommandHandler(IApplicationDbContext _context, IPasswordHasher _passwordHasher)
    {
        this._context = _context;
        this._passwordHasher = _passwordHasher;
    }

    public async Task<(bool Success, string? Error)> Handle(UpdateStaffCommand request, CancellationToken cancellationToken)
    {
        var user = await _context.Users.FindAsync(new object[] { request.UserId }, cancellationToken);
        if (user == null) return (false, "User not found.");

        var mapping = await _context.UserHospitalMappings
            .Include(m => m.Roles)
            .Include(m => m.CustomRoles)
            .FirstOrDefaultAsync(m => m.UserId == request.UserId && m.HospitalId == request.HospitalId, cancellationToken);

        if (mapping == null) return (false, "Staff mapping for this hospital not found.");

        // 1. Update Identity
        user.FullName = request.FullName;
        user.Specialization = request.Specialization;
        user.Degree = request.Degree;
        user.LicenseNo = request.LicenseNo;

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            user.Password = request.Password;
            user.PasswordHash = _passwordHasher.Hash(request.Password);
        }

        // 2. Resolve & Update Roles
        var normalizedRoleNames = request.RoleNames.Select(rn => rn.Trim().ToLower()).ToList();
        var roles = await _context.Roles
            .Where(r => normalizedRoleNames.Contains(r.RoleName.ToLower()))
            .ToListAsync(cancellationToken);

        var customRoles = await _context.CustomRoles
            .Where(cr => cr.HospitalId == request.HospitalId && normalizedRoleNames.Contains(cr.RoleName.ToLower()))
            .ToListAsync(cancellationToken);

        mapping.Roles.Clear();
        foreach (var role in roles) mapping.Roles.Add(role);

        mapping.CustomRoles.Clear();
        foreach (var cr in customRoles) mapping.CustomRoles.Add(cr);

        await _context.SaveChangesAsync(cancellationToken);
        return (true, null);
    }
}
