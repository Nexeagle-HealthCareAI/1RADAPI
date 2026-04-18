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
            request.NABHNumber));

        if (!result.Success) return BadRequest(new { message = result.Error });
        return Ok(new { message = "Hospital metadata updated successfully." });
    }
}

public record UpdateHospitalDetailsRequest(
    string HospitalName,
    string HospitalAddress,
    string? GSTIN,
    string? RegistrationNumber,
    string? PAN,
    string? NABHNumber);
