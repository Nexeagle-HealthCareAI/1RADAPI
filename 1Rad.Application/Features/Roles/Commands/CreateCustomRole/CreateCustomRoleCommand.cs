using MediatR;

namespace _1Rad.Application.Features.Roles.Commands.CreateCustomRole;

public record CreateCustomRoleCommand(
    Guid HospitalId,
    string RoleName,
    string? Description,
    List<string> Permissions) : IRequest<(Guid RoleId, string? Error)>;
