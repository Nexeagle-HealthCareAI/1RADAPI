using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Personnel.Commands.RegisterStaff;

public class RegisterStaffCommandHandler : IRequestHandler<RegisterStaffCommand, (Guid UserId, string? Error)>
{
    private readonly IApplicationDbContext _context;
    private readonly IPasswordHasher _passwordHasher;

    public RegisterStaffCommandHandler(IApplicationDbContext _context, IPasswordHasher _passwordHasher)
    {
        this._context = _context;
        this._passwordHasher = _passwordHasher;
    }

    public async Task<(Guid UserId, string? Error)> Handle(RegisterStaffCommand request, CancellationToken cancellationToken)
    {
        // 1. Validate Hospital
        var hospital = await _context.Hospitals.FindAsync(new object[] { request.HospitalId }, cancellationToken);
        if (hospital == null) return (Guid.Empty, "Hospital not found.");

        // 2. Resolve Roles
        var normalizedRequestRoles = request.RoleNames.Select(r => r.Trim().ToLower()).ToList();
        var roles = await _context.Roles
            .Where(r => normalizedRequestRoles.Contains(r.RoleName.ToLower()))
            .ToListAsync(cancellationToken);
        
        if (!roles.Any())
        {
            // Fallback for strict database collations: Fetch and filter in-memory if query returns nothing
            var allRoles = await _context.Roles.ToListAsync(cancellationToken);
            roles = allRoles.Where(r => normalizedRequestRoles.Contains(r.RoleName.ToLower())).ToList();
        }

        if (!roles.Any()) return (Guid.Empty, "Invalid roles selected. Please verify system role naming.");

        // 3. Check for existing User
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Email == request.Email || u.Mobile == request.Mobile, cancellationToken);

        if (user == null)
        {
            user = new User
            {
                FullName = request.FullName,
                Email = request.Email,
                Mobile = request.Mobile,
                PasswordHash = _passwordHasher.Hash(request.Password),
                Status = UserStatus.Active, // Admin created users are active by default
                IsVerified = true,
                CreatedAt = DateTime.UtcNow,
                Specialization = request.Specialization,
                Degree = request.Degree,
                LicenseNo = request.LicenseNo
            };
            _context.Users.Add(user);
        }

        // 4. Check for existing Mapping
        var existingMapping = await _context.UserHospitalMappings
            .Include(m => m.Roles)
            .FirstOrDefaultAsync(m => m.UserId == user.UserId && m.HospitalId == request.HospitalId, cancellationToken);

        if (existingMapping != null)
        {
            // Update roles if they differ
            existingMapping.Roles.Clear();
            foreach (var role in roles) existingMapping.Roles.Add(role);
        }
        else
        {
            var mapping = new UserHospitalMapping
            {
                UserId = user.UserId,
                HospitalId = request.HospitalId,
                AssignedAt = DateTime.UtcNow,
                Roles = roles
            };
            _context.UserHospitalMappings.Add(mapping);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return (user.UserId, null);
    }
}
