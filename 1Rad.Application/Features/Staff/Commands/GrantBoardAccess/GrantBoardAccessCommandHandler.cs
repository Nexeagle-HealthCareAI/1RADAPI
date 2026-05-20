using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Commands.GrantBoardAccess;

public class GrantBoardAccessCommandHandler : IRequestHandler<GrantBoardAccessCommand, (bool Success, string? Error)>
{
    private readonly IApplicationDbContext _context;
    private readonly IPasswordHasher _passwordHasher;

    public GrantBoardAccessCommandHandler(IApplicationDbContext context, IPasswordHasher passwordHasher)
    {
        _context = context;
        _passwordHasher = passwordHasher;
    }

    public async Task<(bool Success, string? Error)> Handle(GrantBoardAccessCommand request, CancellationToken cancellationToken)
    {
        var member = await _context.StaffMembers
            .FirstOrDefaultAsync(s => s.StaffId == request.StaffId && s.HospitalId == request.HospitalId, cancellationToken);

        if (member == null)
            return (false, "Staff member not found.");

        if (member.BoardAccessUserId.HasValue)
            return (false, "Board access is already granted for this staff member.");

        // Resolve board roles
        var normalizedRoles = request.RoleNames.Select(r => r.Trim().ToLower()).ToList();
        var roles = await _context.Roles
            .Where(r => normalizedRoles.Contains(r.RoleName.ToLower()))
            .ToListAsync(cancellationToken);

        if (!roles.Any())
            return (false, "At least one valid role must be specified.");

        // Reuse an existing user account if email/mobile already registered
        User? user = null;
        if (!string.IsNullOrWhiteSpace(member.Email))
            user = await _context.Users.FirstOrDefaultAsync(u => u.Email == member.Email, cancellationToken);
        if (user == null && !string.IsNullOrWhiteSpace(member.Mobile))
            user = await _context.Users.FirstOrDefaultAsync(u => u.Mobile == member.Mobile, cancellationToken);

        if (user == null)
        {
            user = new User
            {
                FullName       = member.FullName,
                Email          = member.Email ?? $"staff_{member.StaffId:N}@noemail.local",
                Mobile         = member.Mobile ?? string.Empty,
                Password       = request.Password,
                PasswordHash   = _passwordHasher.Hash(request.Password),
                Status         = UserStatus.Active,
                IsVerified     = true,
                Specialization = member.Specialization,
                Degree         = member.Degree,
                LicenseNo      = member.LicenseNo,
            };
            _context.Users.Add(user);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(request.Password))
            {
                user.Password     = request.Password;
                user.PasswordHash = _passwordHasher.Hash(request.Password);
            }
            user.Status     = UserStatus.Active;
            user.IsVerified = true;
        }

        // Create hospital mapping
        var mapping = new UserHospitalMapping
        {
            UserId     = user.UserId,
            HospitalId = request.HospitalId,
            IsDefault  = true,
        };
        foreach (var role in roles) mapping.Roles.Add(role);
        _context.UserHospitalMappings.Add(mapping);

        // Link back to the staff record
        member.BoardAccessUserId = user.UserId;
        member.UpdatedAt         = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return (true, null);
    }
}
