using MediatR;

namespace _1Rad.Application.Features.Personnel.Commands.UpdateStaff;

public record UpdateStaffCommand(
    Guid UserId,
    Guid HospitalId,
    string FullName,
    List<string> RoleNames,
    string? Specialization = null,
    string? Degree = null,
    string? LicenseNo = null) : IRequest<(bool Success, string? Error)>;
