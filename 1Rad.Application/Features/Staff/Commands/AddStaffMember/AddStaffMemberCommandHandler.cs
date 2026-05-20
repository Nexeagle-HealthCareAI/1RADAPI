using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Commands.AddStaffMember;

public class AddStaffMemberCommandHandler : IRequestHandler<AddStaffMemberCommand, (Guid StaffId, string? Error)>
{
    private readonly IApplicationDbContext _context;

    public AddStaffMemberCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(Guid StaffId, string? Error)> Handle(AddStaffMemberCommand request, CancellationToken cancellationToken)
    {
        var hospital = await _context.Hospitals.FindAsync(new object[] { request.HospitalId }, cancellationToken);
        if (hospital == null)
            return (Guid.Empty, "Hospital not found.");

        DateOnly? joiningDate = null;
        if (!string.IsNullOrWhiteSpace(request.JoiningDate) &&
            DateOnly.TryParse(request.JoiningDate, out var parsedDate))
            joiningDate = parsedDate;

        var member = new StaffMember
        {
            HospitalId     = request.HospitalId,
            FullName       = request.FullName.Trim(),
            Email          = request.Email?.Trim(),
            Mobile         = request.Mobile?.Trim(),
            Designation    = request.Designation?.Trim(),
            Department     = request.Department?.Trim(),
            EmploymentType = request.EmploymentType ?? "Full-Time",
            Specialization = request.Specialization?.Trim(),
            Degree         = request.Degree?.Trim(),
            LicenseNo      = request.LicenseNo?.Trim(),
            JoiningDate    = joiningDate,
            Status         = "Active",
        };

        foreach (var roleName in request.RoleNames.Where(r => !string.IsNullOrWhiteSpace(r)))
            member.Roles.Add(new StaffMemberRole { RoleName = roleName.Trim() });

        _context.StaffMembers.Add(member);
        await _context.SaveChangesAsync(cancellationToken);

        return (member.StaffId, null);
    }
}
