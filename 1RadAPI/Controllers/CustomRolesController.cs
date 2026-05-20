using _1Rad.Application.Features.Roles.Commands.CreateCustomRole;
using _1Rad.Application.Features.Roles.Commands.DeleteCustomRole;
using _1Rad.Application.Features.Roles.Commands.UpdateCustomRole;
using _1Rad.Application.Features.Roles.Queries;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class CustomRolesController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IUserContext _userContext;

    public CustomRolesController(IMediator mediator, IUserContext userContext)
    {
        _mediator = mediator;
        _userContext = userContext;
    }

    [HttpGet]
    public async Task<IActionResult> GetCustomRoles()
    {
        var hospitalId = _userContext.HospitalId;
        if (hospitalId == Guid.Empty) return BadRequest(new { message = "Hospital context missing." });

        var result = await _mediator.Send(new GetCustomRolesQuery(hospitalId));
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> CreateCustomRole([FromBody] CreateCustomRoleRequest request)
    {
        var hospitalId = _userContext.HospitalId;
        if (hospitalId == Guid.Empty) return BadRequest(new { message = "Hospital context missing." });

        var command = new CreateCustomRoleCommand(hospitalId, request.RoleName, request.Description, request.Permissions);
        var (roleId, error) = await _mediator.Send(command);

        if (error != null) return BadRequest(new { message = error });
        return Ok(new { roleId, message = "Custom role created successfully." });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateCustomRole(Guid id, [FromBody] UpdateCustomRoleRequest request)
    {
        var hospitalId = _userContext.HospitalId;
        if (hospitalId == Guid.Empty) return BadRequest(new { message = "Hospital context missing." });

        var command = new UpdateCustomRoleCommand(id, hospitalId, request.RoleName, request.Description, request.Permissions);
        var (success, error) = await _mediator.Send(command);

        if (!success) return BadRequest(new { message = error });
        return Ok(new { message = "Custom role updated successfully." });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteCustomRole(Guid id)
    {
        var hospitalId = _userContext.HospitalId;
        if (hospitalId == Guid.Empty) return BadRequest(new { message = "Hospital context missing." });

        var command = new DeleteCustomRoleCommand(id, hospitalId);
        var (success, error) = await _mediator.Send(command);

        if (!success) return BadRequest(new { message = error });
        return Ok(new { message = "Custom role deleted successfully." });
    }
}

public record CreateCustomRoleRequest(string RoleName, string? Description, List<string> Permissions);
public record UpdateCustomRoleRequest(string RoleName, string? Description, List<string> Permissions);
