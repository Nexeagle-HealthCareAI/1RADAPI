using _1Rad.Application.Features.Staff.Common;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Staff.Queries.GetHospitalStaff;

public class GetHospitalStaffQueryHandler : IRequestHandler<GetHospitalStaffQuery, List<StaffMemberDto>>
{
    private readonly IApplicationDbContext _context;

    public GetHospitalStaffQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<StaffMemberDto>> Handle(GetHospitalStaffQuery request, CancellationToken cancellationToken)
    {
        // One-time backfill for any legacy staff without an EmployeeCode.
        await EmployeeCodeGenerator.BackfillMissingCodesAsync(_context, request.HospitalId, cancellationToken);

        var members = await _context.StaffMembers
            .Where(s => s.HospitalId == request.HospitalId)
            .Include(s => s.Roles)
            .OrderBy(s => s.FullName)
            .ToListAsync(cancellationToken);

        return members.Select(s => new StaffMemberDto(
            s.StaffId,
            s.EmployeeCode,
            s.FullName,
            s.Email,
            s.Mobile,
            s.Designation,
            s.Department,
            s.EmploymentType,
            s.Roles.Select(r => r.RoleName).ToList(),
            s.Specialization,
            s.Degree,
            s.LicenseNo,
            s.JoiningDate?.ToString("yyyy-MM-dd"),
            s.Status,
            s.BoardAccessUserId,
            s.CreatedAt,
            s.UpdatedAt
        )).ToList();
    }
}
