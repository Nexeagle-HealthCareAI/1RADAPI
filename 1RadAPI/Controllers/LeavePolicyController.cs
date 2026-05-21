using _1Rad.Application.Features.LeavePolicy.Commands.SaveLeavePolicy;
using _1Rad.Application.Features.LeavePolicy.Queries.GetLeavePolicy;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/leave-policy")]
public class LeavePolicyController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IUserContext _userContext;

    public LeavePolicyController(IMediator mediator, IUserContext userContext)
    {
        _mediator = mediator;
        _userContext = userContext;
    }

    // GET /api/v1/leave-policy
    [HttpGet]
    public async Task<IActionResult> GetPolicy()
    {
        var hospitalId = _userContext.HospitalId;
        if (hospitalId == Guid.Empty) return BadRequest(new { message = "Hospital context missing." });

        var result = await _mediator.Send(new GetLeavePolicyQuery(hospitalId));
        return Ok(result);
    }

    // PUT /api/v1/leave-policy
    [HttpPut]
    public async Task<IActionResult> SavePolicy([FromBody] SaveLeavePolicyRequest body)
    {
        var hospitalId = _userContext.HospitalId;
        if (hospitalId == Guid.Empty) return BadRequest(new { message = "Hospital context missing." });

        var cmd = new SaveLeavePolicyCommand(hospitalId, _userContext.UserId, body.LeaveTypesJson ?? "[]");
        var (policyId, error) = await _mediator.Send(cmd);
        return error == null
            ? Ok(new { policyId, message = "Leave policy saved." })
            : BadRequest(new { message = error });
    }
}

public record SaveLeavePolicyRequest(string? LeaveTypesJson);
