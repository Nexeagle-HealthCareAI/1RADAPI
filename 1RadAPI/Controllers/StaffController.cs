using _1Rad.Application.Features.Staff.Commands.AddStaffMember;
using _1Rad.Application.Features.Staff.Commands.GrantBoardAccess;
using _1Rad.Application.Features.Staff.Commands.RemoveStaffMember;
using _1Rad.Application.Features.Staff.Commands.UpdateStaffMember;
using _1Rad.Application.Features.Staff.Queries.GetHospitalStaff;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class StaffController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IUserContext _userContext;

    public StaffController(IMediator mediator, IUserContext userContext)
    {
        _mediator = mediator;
        _userContext = userContext;
    }

    // GET /api/v1/staff
    [HttpGet]
    public async Task<IActionResult> GetStaff()
    {
        var hospitalId = _userContext.HospitalId;
        if (hospitalId == Guid.Empty) return BadRequest("Hospital context missing.");

        var result = await _mediator.Send(new GetHospitalStaffQuery(hospitalId));
        return Ok(result);
    }

    // POST /api/v1/staff
    [HttpPost]
    public async Task<IActionResult> AddStaff([FromBody] AddStaffMemberCommand command)
    {
        var cmd = command with { HospitalId = _userContext.HospitalId };
        var (staffId, error) = await _mediator.Send(cmd);
        return error == null
            ? Ok(new { staffId, message = "Staff member added successfully." })
            : BadRequest(new { message = error });
    }

    // PUT /api/v1/staff/{id}
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateStaff(Guid id, [FromBody] UpdateStaffMemberCommand command)
    {
        var cmd = command with { StaffId = id, HospitalId = _userContext.HospitalId };
        var (success, error) = await _mediator.Send(cmd);
        return success
            ? Ok(new { message = "Staff member updated successfully." })
            : BadRequest(new { message = error });
    }

    // DELETE /api/v1/staff/{id}
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> RemoveStaff(Guid id)
    {
        var (success, error) = await _mediator.Send(new RemoveStaffMemberCommand(id, _userContext.HospitalId));
        return success
            ? Ok(new { message = "Staff member removed." })
            : BadRequest(new { message = error });
    }

    // POST /api/v1/staff/{id}/grant-access
    [HttpPost("{id:guid}/grant-access")]
    public async Task<IActionResult> GrantAccess(Guid id, [FromBody] GrantAccessRequest body)
    {
        var cmd = new GrantBoardAccessCommand(id, _userContext.HospitalId, body.Password, body.RoleNames);
        var (success, error) = await _mediator.Send(cmd);
        return success
            ? Ok(new { message = "Board access granted." })
            : BadRequest(new { message = error });
    }
}

public record GrantAccessRequest(string Password, List<string> RoleNames);
