using _1Rad.Application.Features.Hospitals.Commands.CreateChain;
using _1Rad.Application.Features.Hospitals.Queries.GetGroupHospitals;
using _1Rad.Application.Features.Hospitals.Commands.UpdateHospitalDetails;
using _1Rad.Application.Features.Hospitals.Queries.GetHospitalDetails;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class HospitalsController : ControllerBase
{
    private readonly IMediator _mediator;

    public HospitalsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetHospital(Guid id)
    {
        var result = await _mediator.Send(new GetHospitalDetailsQuery(id));
        if (result == null) return NotFound();
        return Ok(result);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateHospital(Guid id, [FromBody] UpdateHospitalDetailsRequest request)
    {
        var result = await _mediator.Send(new UpdateHospitalDetailsCommand(
            id,
            request.HospitalName,
            request.HospitalAddress,
            request.GSTIN,
            request.RegistrationNumber,
            request.PAN,
            request.NABHNumber,
            request.IsAutoBillingEnabled));

        if (!result.Success) return BadRequest(new { message = result.Error });
        return Ok(new { message = "Hospital metadata updated successfully." });
    }

    [HttpPost("chain")]
    public async Task<IActionResult> CreateChain([FromBody] CreateChainRequest request)
    {
        var userId = Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? Guid.Empty.ToString());
        if (userId == Guid.Empty) return Unauthorized();

        var result = await _mediator.Send(new CreateChainCommand(
            userId,
            request.ChainName,
            request.HospitalName,
            request.HospitalAddress,
            request.GSTIN,
            request.RegistrationNumber,
            request.PAN,
            request.NABHNumber,
            request.Modules));

        if (!result.Success) return BadRequest(new { success = false, message = result.Error, errorCode = result.ErrorCode });
        return Ok(result);
    }

    [HttpGet("group")]
    public async Task<IActionResult> GetGroupHospitals()
    {
        var result = await _mediator.Send(new GetGroupHospitalsQuery());
        return Ok(result);
    }
}

public class UpdateHospitalDetailsRequest
{
    public string HospitalName { get; set; } = string.Empty;
    public string HospitalAddress { get; set; } = string.Empty;
    public string? GSTIN { get; set; }
    public string? RegistrationNumber { get; set; }
    public string? PAN { get; set; }
    public string? NABHNumber { get; set; }
    public bool IsAutoBillingEnabled { get; set; }
}

public record CreateChainRequest(
    string ChainName,
    string HospitalName,
    string HospitalAddress,
    string? GSTIN = null,
    string? RegistrationNumber = null,
    string? PAN = null,
    string? NABHNumber = null,
    // Chosen product package (SKU) for the new centre: "RIS" | "PACS" | "RIS,PACS".
    string? Modules = null);
