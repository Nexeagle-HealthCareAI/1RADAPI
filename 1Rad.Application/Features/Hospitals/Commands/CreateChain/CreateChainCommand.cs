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
    string? NABHNumber = null,
    // Chosen product package (SKU) for the NEW centre: "RIS", "PACS", or
    // "RIS,PACS". Modules are per-centre, so each new centre in a chain can run a
    // different SKU. Null/invalid falls back to the default (full) product.
    string? Modules = null) : IRequest<CreateChainResponse>;

public class CreateChainResponse
{
    public bool Success { get; set; }
    public Guid? HospitalId { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}
