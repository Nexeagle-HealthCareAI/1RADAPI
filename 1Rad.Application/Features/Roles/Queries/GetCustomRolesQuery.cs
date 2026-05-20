using MediatR;

namespace _1Rad.Application.Features.Roles.Queries;

public record CustomRoleDto(Guid RoleId, string RoleName, string? Description, List<string> Permissions);

public record GetCustomRolesQuery(Guid HospitalId) : IRequest<List<CustomRoleDto>>;
