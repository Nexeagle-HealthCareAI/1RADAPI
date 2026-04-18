using MediatR;

namespace _1Rad.Application.Features.Auth.Commands.DeployInfrastructure;

public record DeployInfrastructureCommand(
    Guid UserId, 
    string ChainName, 
    string HospitalName, 
    string HospitalAddress, 
    int RoleId) : IRequest<(bool Success, string? Error)>;
