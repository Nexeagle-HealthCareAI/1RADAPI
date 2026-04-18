using MediatR;


namespace _1Rad.Application.Features.Auth.Commands.DeployInfrastructure;

public record DeployInfrastructureCommand(
    Guid UserId, 
    string ChainName, 
    string HospitalName, 
    string HospitalAddress, 
    string RoleName,
    string? GSTINNumber = null,
    string? RegistrationNumber = null,
    string? PANNumber = null,
    string? NABHNumber = null,
    string? Specialization = null,
    string? Degree = null,
    string? LicenseNo = null) : IRequest<(bool Success, string? Error)>;
