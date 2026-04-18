using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Personnel.Commands.UpdateStaff;

public class UpdateStaffCommandHandler : IRequestHandler<UpdateStaffCommand, (bool Success, string? Error)>
{
    private readonly IApplicationDbContext _context;

    public UpdateStaffCommandHandler(IApplicationDbContext _context)
    {
        this._context = _context;
    }

    public async Task<(bool Success, string? Error)> Handle(UpdateStaffCommand request, CancellationToken cancellationToken)
    {
        var user = await _context.Users.FindAsync(new object[] { request.UserId }, cancellationToken);
        if (user == null) return (false, "User not found.");

        var mapping = await _context.UserHospitalMappings
            .Include(m => m.Roles)
            .FirstOrDefaultAsync(m => m.UserId == request.UserId && m.HospitalId == request.HospitalId, cancellationToken);

        if (mapping == null) return (false, "Staff mapping for this hospital not found.");

        // 1. Update Identity
        user.FullName = request.FullName;
        user.Specialization = request.Specialization;
        user.Degree = request.Degree;
        user.LicenseNo = request.LicenseNo;

        // 2. Resolve & Update Roles
        var roles = await _context.Roles
            .Where(r => request.RoleNames.Contains(r.RoleName.ToLower()))
            .ToListAsync(cancellationToken);

        mapping.Roles.Clear();
        foreach (var role in roles) mapping.Roles.Add(role);

        await _context.SaveChangesAsync(cancellationToken);
        return (true, null);
    }
}
