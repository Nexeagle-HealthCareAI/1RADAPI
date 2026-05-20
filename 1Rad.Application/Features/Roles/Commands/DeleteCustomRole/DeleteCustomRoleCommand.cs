using MediatR;

namespace _1Rad.Application.Features.Roles.Commands.DeleteCustomRole;

public record DeleteCustomRoleCommand(Guid RoleId, Guid HospitalId) : IRequest<(bool Success, string? Error)>;
