using _1Rad.Application.Features.Personnel.Commands.RegisterStaff;
using _1Rad.Application.Features.Personnel.Commands.RemoveStaff;
using _1Rad.Application.Features.Personnel.Commands.UpdatePreference;
using _1Rad.Application.Features.Personnel.Commands.UpdateStaff;
using _1Rad.Application.Features.Personnel.Queries.GetHospitalPersonnel;
using _1Rad.Application.Features.Users.Commands.UpdateClinicalCredentials;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class PersonnelController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IUserContext _userContext;

    public PersonnelController(IMediator mediator, IUserContext userContext)
    {
        _mediator = mediator;
        _userContext = userContext;
    }

    [HttpGet]
    public async Task<IActionResult> GetPersonnel()
    {
        var hospitalId = _userContext.HospitalId;
        if (hospitalId == Guid.Empty) return BadRequest("Hospital context missing.");

        var result = await _mediator.Send(new GetHospitalPersonnelQuery(hospitalId));
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> RegisterStaff([FromBody] RegisterStaffCommand command)
    {
        // Enforce hospital context from token if not explicitly provided or for security
        var commandWithContext = command with { HospitalId = _userContext.HospitalId };
        
        var (userId, error) = await _mediator.Send(commandWithContext);
        return error == null 
            ? Ok(new { userId, message = "Staff registered successfully." }) 
            : BadRequest(new { message = error });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateStaff(Guid id, [FromBody] UpdateStaffCommand command)
    {
        var commandWithId = command with { UserId = id, HospitalId = _userContext.HospitalId };
        var (success, error) = await _mediator.Send(commandWithId);
        
        return success 
            ? Ok(new { message = "Staff updated successfully." }) 
            : BadRequest(new { message = error });
    }

    [HttpPatch("preferences")]
    public async Task<IActionResult> UpdatePreference([FromBody] UpdatePreferenceCommand command)
    {
        var result = await _mediator.Send(command);
        return result ? Ok(new { success = true }) : BadRequest(new { message = "Failed to update preference." });
    }

    /// <summary>
    /// Update a user's clinical credentials only (specialization, degree,
    /// license number). Used by the Hospital Management screen to edit the
    /// primary admin's clinical profile without touching account fields.
    /// </summary>
    [HttpPatch("{id}/clinical-credentials")]
    public async Task<IActionResult> UpdateClinicalCredentials(Guid id, [FromBody] UpdateClinicalCredentialsBody body)
    {
        var result = await _mediator.Send(new UpdateClinicalCredentialsCommand(
            UserId:         id,
            Specialization: body.Specialization,
            Degree:         body.Degree,
            LicenseNo:      body.LicenseNo
        ));
        return result.Success
            ? Ok(new { success = true, message = "Clinical credentials updated." })
            : BadRequest(new { success = false, error = result.Error, errorCode = result.ErrorCode });
    }

    public record UpdateClinicalCredentialsBody(
        string? Specialization,
        string? Degree,
        string? LicenseNo
    );

    [HttpDelete("{id}")]
    public async Task<IActionResult> RemoveStaff(Guid id)
    {
        var (success, error) = await _mediator.Send(new RemoveStaffCommand(id, _userContext.HospitalId));
        return success 
            ? Ok(new { message = "Staff removed from hospital." }) 
            : BadRequest(new { message = error });
    }
}
