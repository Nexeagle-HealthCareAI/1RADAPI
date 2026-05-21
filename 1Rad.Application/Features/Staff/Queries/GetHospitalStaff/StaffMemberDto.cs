namespace _1Rad.Application.Features.Staff.Queries.GetHospitalStaff;

public record StaffMemberDto(
    Guid StaffId,
    string? EmployeeCode,
    string FullName,
    string? Email,
    string? Mobile,
    string? Designation,
    string? Department,
    string EmploymentType,
    List<string> RoleNames,
    string? Specialization,
    string? Degree,
    string? LicenseNo,
    string? JoiningDate,
    string Status,
    Guid? BoardAccessUserId,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);
