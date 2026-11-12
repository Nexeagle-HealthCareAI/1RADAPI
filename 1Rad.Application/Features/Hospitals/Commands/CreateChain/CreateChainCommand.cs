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
    string? Modules = null,
    // Owner board: start with this centre's billing rules and prices (the user must
    // be an admin there). KeepLinked = the new centre keeps following it.
    Guid? CopySetupFromHospitalId = null,
    bool KeepLinked = true) : IRequest<CreateChainResponse>;

public class CreateChainResponse
{
    public bool Success { get; set; }
    public Guid? HospitalId { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    /// <summary>True when the setup was copied from the chosen centre.</summary>
    public bool SetupCopied { get; set; }
    /// <summary>The centre was made, but copying its setup failed (it can be copied again from the owner board).</summary>
    public string? SetupCopyError { get; set; }
}
