using MediatR;
using System;

namespace _1Rad.Application.Features.Hospitals.Commands.CreateChain;

public record CreateChainCommand(
    Guid UserId,
    string ChainName,
    string HospitalName,
    string HospitalAddress,
    string? GSTIN = null,
    string? RegistrationNumber = null,
    string? PAN = null,
    string? NABHNumber = null) : IRequest<CreateChainResponse>;

public class CreateChainResponse
{
    public bool Success { get; set; }
    public Guid? HospitalId { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}
