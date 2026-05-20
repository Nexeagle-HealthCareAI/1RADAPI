using MediatR;

namespace _1Rad.Application.Features.Staff.Commands.UpdateStaffMember;

public record UpdateStaffMemberCommand(
    Guid StaffId,
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
    string? JoiningDate,
    string? Status
) : IRequest<(bool Success, string? Error)>;
