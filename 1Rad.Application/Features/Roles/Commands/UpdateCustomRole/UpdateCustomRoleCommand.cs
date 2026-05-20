using MediatR;

namespace _1Rad.Application.Features.Roles.Commands.UpdateCustomRole;

public record UpdateCustomRoleCommand(
    Guid RoleId,
    Guid HospitalId,
    string RoleName,
    string? Description,
    List<string> Permissions) : IRequest<(bool Success, string? Error)>;
