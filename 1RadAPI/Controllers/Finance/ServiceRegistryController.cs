using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using _1Rad.Application.Features.Finance.Commands.UpsertServiceCharge;
using _1Rad.Application.Features.Finance.Commands.AddBookingService;
using _1Rad.Application.Features.Finance.Commands.DeleteServiceCharge;
using _1Rad.Application.Features.Finance.Queries.GetServiceCharges;
using _1Rad.Domain.Constants;

namespace _1RadAPI.Controllers.Finance;

/// <summary>
/// Service catalogue management.
/// Keeps price/modality config isolated from billing transaction controllers.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/finance")]
[_1RadAPI.Authorization.RequiresModule(ModuleConstants.Ris)]
public class ServiceRegistryController : ControllerBase
{
    private readonly IMediator _mediator;

    public ServiceRegistryController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("registry")]
    public async Task<IActionResult> GetRegistry()
    {
        var result = await _mediator.Send(new GetServiceChargesQuery());
        return Ok(result);
    }

    [HttpPost("registry")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator}")]
    public async Task<IActionResult> UpsertRegistry([FromBody] UpsertServiceChargeCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { id = result });
    }

    [HttpPut("registry/{id}")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator}")]
    public async Task<IActionResult> UpdateRegistry(Guid id, [FromBody] UpsertServiceChargeCommand command)
    {
        var updatedCommand = command with { Id = id };
        var result = await _mediator.Send(updatedCommand);
        return Ok(new { id = result });
    }

    /// <summary>
    /// Add a service to the catalogue on the fly during appointment booking when
    /// the chosen modality has no service yet. Smart upsert (create-with-template /
    /// price an unpriced service / return an already-priced one). Restricted to the
    /// roles that actually book — front desk + admins. Tenant is taken from the
    /// auth context inside the handler, never the client.
    /// </summary>
    [HttpPost("registry/quick-add")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Receptionist}")]
    public async Task<IActionResult> QuickAddRegistry([FromBody] AddBookingServiceCommand command)
    {
        try
        {
            var dto = await _mediator.Send(command);
            return Ok(new { success = true, data = dto });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { success = false, error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, error = $"Failed to add the service: {ex.Message}" });
        }
    }

    [HttpDelete("registry/{id}")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator}")]
    public async Task<IActionResult> DeleteRegistry(Guid id)
    {
        await _mediator.Send(new DeleteServiceChargeCommand(id));
        return Ok();
    }
}
