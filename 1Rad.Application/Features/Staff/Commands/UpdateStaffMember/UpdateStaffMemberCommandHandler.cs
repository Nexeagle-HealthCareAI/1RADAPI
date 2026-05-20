using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Commands.UpdateStaffMember;

public class UpdateStaffMemberCommandHandler : IRequestHandler<UpdateStaffMemberCommand, (bool Success, string? Error)>
{
    private readonly IApplicationDbContext _context;

    public UpdateStaffMemberCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(bool Success, string? Error)> Handle(UpdateStaffMemberCommand request, CancellationToken cancellationToken)
    {
        var member = await _context.StaffMembers
            .Include(s => s.Roles)
            .FirstOrDefaultAsync(s => s.StaffId == request.StaffId && s.HospitalId == request.HospitalId, cancellationToken);

        if (member == null)
            return (false, "Staff member not found.");

        DateOnly? joiningDate = null;
        if (!string.IsNullOrWhiteSpace(request.JoiningDate) &&
            DateOnly.TryParse(request.JoiningDate, out var parsedDate))
            joiningDate = parsedDate;

        member.FullName       = request.FullName.Trim();
        member.Email          = request.Email?.Trim();
        member.Mobile         = request.Mobile?.Trim();
        member.Designation    = request.Designation?.Trim();
        member.Department     = request.Department?.Trim();
        member.EmploymentType = request.EmploymentType ?? member.EmploymentType;
        member.Specialization = request.Specialization?.Trim();
        member.Degree         = request.Degree?.Trim();
        member.LicenseNo      = request.LicenseNo?.Trim();
        member.JoiningDate    = joiningDate;
        member.Status         = request.Status ?? member.Status;
        member.UpdatedAt      = DateTime.UtcNow;

        // Replace roles
        member.Roles.Clear();
        foreach (var roleName in request.RoleNames.Where(r => !string.IsNullOrWhiteSpace(r)))
            member.Roles.Add(new StaffMemberRole { RoleName = roleName.Trim() });

        await _context.SaveChangesAsync(cancellationToken);
        return (true, null);
    }
}
