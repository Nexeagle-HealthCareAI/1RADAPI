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
            request.NABHNumber));

        if (!result.Success) return BadRequest(new { message = result.Error });
        return Ok(result);
    }

    [HttpGet("group")]
    public async Task<IActionResult> GetGroupHospitals()
    {
        var result = await _mediator.Send(new GetGroupHospitalsQuery());
        return Ok(result);
    }
}

public record UpdateHospitalDetailsRequest(
    string HospitalName,
    string HospitalAddress,
    string? GSTIN,
    string? RegistrationNumber,
    string? PAN,
    string? NABHNumber,
    bool IsAutoBillingEnabled = false);

public record CreateChainRequest(
    string ChainName,
    string HospitalName,
    string HospitalAddress,
    string? GSTIN = null,
    string? RegistrationNumber = null,
    string? PAN = null,
    string? NABHNumber = null);
