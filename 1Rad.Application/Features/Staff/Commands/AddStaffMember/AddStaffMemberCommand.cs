using MediatR;

namespace _1Rad.Application.Features.Staff.Commands.AddStaffMember;

public record AddStaffMemberCommand(
    Guid HospitalId,
    string FullName,
    string? Email,
    string? Mobile,
    string? Designation,
    string? Department,
    string? EmploymentType,
    List<string> RoleNames,
    string? Specialization,
    string? Degree,
    string? LicenseNo,
    string? JoiningDate
) : IRequest<(Guid StaffId, string? Error)>;
