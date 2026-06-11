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
    string? LicenseNo = null,
    // Chosen product package at sign-up: "RIS", "PACS", or "RIS,PACS".
    // Null/invalid falls back to the full product.
    string? Modules = null) : IRequest<DeployInfrastructureResponse>;

public class DeployInfrastructureResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}
