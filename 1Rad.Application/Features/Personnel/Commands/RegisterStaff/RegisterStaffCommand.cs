using MediatR;

namespace _1Rad.Application.Features.Personnel.Commands.RegisterStaff;

public record RegisterStaffCommand(
    Guid HospitalId,
    string FullName,
    string Email,
    string Mobile,
    string Password,
    List<string> RoleNames,
    string? Specialization = null,
    string? Degree = null,
    string? LicenseNo = null) : IRequest<(Guid UserId, string? Error)>;
