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
    private readonly ISubscriptionLimitsService _limits;

    public RegisterStaffCommandHandler(IApplicationDbContext _context, IPasswordHasher _passwordHasher, ISubscriptionLimitsService limits)
    {
        this._context = _context;
        this._passwordHasher = _passwordHasher;
        this._limits = limits;
    }

    public async Task<(Guid UserId, string? Error)> Handle(RegisterStaffCommand request, CancellationToken cancellationToken)
    {
        // 1. Validate Hospital
        var hospital = await _context.Hospitals.FindAsync(new object[] { request.HospitalId }, cancellationToken);
        if (hospital == null) return (Guid.Empty, "Hospital not found.");

        // 2. Resolve Roles (System Roles & Custom Roles)
        var normalizedRequestRoles = request.RoleNames.Select(r => r.Trim().ToLower()).ToList();
        var roles = await _context.Roles
            .Where(r => normalizedRequestRoles.Contains(r.RoleName.ToLower()))
            .ToListAsync(cancellationToken);
        
        if (!roles.Any() && request.RoleNames.Any())
        {
            // Fallback for strict database collations: Fetch and filter in-memory if query returns nothing
            var allRoles = await _context.Roles.ToListAsync(cancellationToken);
            roles = allRoles.Where(r => normalizedRequestRoles.Contains(r.RoleName.ToLower())).ToList();
        }

        var customRoles = await _context.CustomRoles
            .Where(cr => cr.HospitalId == request.HospitalId && normalizedRequestRoles.Contains(cr.RoleName.ToLower()))
            .ToListAsync(cancellationToken);

        if (!roles.Any() && !customRoles.Any()) 
            return (Guid.Empty, "Invalid roles selected. Please verify system/custom role naming.");

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
                Password = request.Password,
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
        else
        {
            // Existing user — e.g. the same person already works at another centre
            // in the chain, so we're adding them to THIS centre. Activate the seat
            // and (only if provided) set their password, but DON'T overwrite their
            // canonical identity: a typo'd email that happens to match someone else
            // must not silently rename that person. Only fill a blank name.
            if (string.IsNullOrWhiteSpace(user.FullName)) user.FullName = request.FullName;
            user.Status = UserStatus.Active;
            user.IsVerified = true;
            if (!string.IsNullOrWhiteSpace(request.Password))
            {
                user.Password = request.Password;
                user.PasswordHash = _passwordHasher.Hash(request.Password);
            }
            if (!string.IsNullOrWhiteSpace(request.Specialization)) user.Specialization = request.Specialization;
            if (!string.IsNullOrWhiteSpace(request.Degree)) user.Degree = request.Degree;
            if (!string.IsNullOrWhiteSpace(request.LicenseNo)) user.LicenseNo = request.LicenseNo;
        }

        // 4. Same-centre duplicate guard. We don't OTP-verify staff emails, so the
        // database IS the source of truth: if this person (matched by email OR
        // mobile above) is already on THIS centre's roster, onboarding is a
        // duplicate — reject it clearly instead of silently re-using/overwriting
        // the existing record (which produced the "two staff, one email" bug).
        // Editing an existing member goes through the dedicated update endpoint.
        var existingMapping = await _context.UserHospitalMappings
            .FirstOrDefaultAsync(m => m.UserId == user.UserId && m.HospitalId == request.HospitalId, cancellationToken);

        if (existingMapping != null)
        {
            return (Guid.Empty, "A staff member with this email or mobile already exists at this centre. Open their profile to edit it instead of adding them again.");
        }

        // New seat — enforce the plan's user cap.
        var seats = await _limits.GetUserLimitAsync(request.HospitalId, cancellationToken);
        if (seats.AtLimit)
            return (Guid.Empty, $"USER_LIMIT_REACHED: Your plan includes {seats.Max} users ({seats.Current} in use). Upgrade your plan to add more staff.");

        var mapping = new UserHospitalMapping
        {
            UserId = user.UserId,
            HospitalId = request.HospitalId,
            AssignedAt = DateTime.UtcNow,
            Roles = roles,
            CustomRoles = customRoles
        };
        _context.UserHospitalMappings.Add(mapping);

        await _context.SaveChangesAsync(cancellationToken);
        return (user.UserId, null);
    }
}
