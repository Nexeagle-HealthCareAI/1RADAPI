using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using _1Rad.Application.Features.Finance.Commands.RecordExpense;
using _1Rad.Application.Features.Finance.Commands.UpdateExpense;
using _1Rad.Application.Features.Finance.Queries.GetExpenses;
using _1Rad.Domain.Constants;

namespace _1RadAPI.Controllers.Finance;

/// <summary>
/// Operational expense ledger. Completely separate from the patient
/// invoice and payment lifecycle.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/finance")]
[_1RadAPI.Authorization.RequiresModule(ModuleConstants.Ris)]
public class ExpenseController : ControllerBase
{
    private readonly IMediator _mediator;

    public ExpenseController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("expenses")]
    public async Task<IActionResult> GetExpenses(
        [FromQuery] string? search,
        [FromQuery] string? category,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] DateTime? updatedAfter,
        [FromQuery] bool includeDeleted = false,
        [FromQuery] int pageSize = 0,
        [FromQuery] string? cursor = null)
    {
        var result = await _mediator.Send(new GetExpensesQuery
        {
            Search = search,
            Category = category,
            StartDate = startDate,
            EndDate = endDate,
            UpdatedAfter = updatedAfter,
            IncludeDeleted = includeDeleted,
            PageSize = pageSize,
            Cursor = cursor,
        });
        // Sync-engine / legacy callers send no pageSize — keep returning a plain array.
        if (!result.IsPaged)
            return Ok(result.Items);
        return Ok(result);
    }

    [HttpPost("expense")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Accountant}")]
    public async Task<IActionResult> RecordExpense([FromBody] RecordExpenseCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { id = result });
    }

    [HttpPut("expenses/{id}")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Accountant}")]
    public async Task<IActionResult> UpdateExpense(Guid id, [FromBody] UpdateExpenseCommand command)
    {
        var updated = command with { Id = id };
        var result = await _mediator.Send(updated);
        return result ? Ok() : NotFound();
    }

    [HttpDelete("expenses/{id}")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Accountant}")]
    public async Task<IActionResult> DeleteExpense(Guid id)
    {
        await _mediator.Send(new _1Rad.Application.Features.Finance.Commands.DeleteExpense.DeleteExpenseCommand(id));
        return Ok();
    }

    [HttpPut("expenses/{id}/status")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Accountant}")]
    public async Task<IActionResult> UpdateExpenseStatus(Guid id, [FromBody] UpdateExpenseStatusRequest request)
    {
        var result = await _mediator.Send(
            new _1Rad.Application.Features.Finance.Commands.UpdateExpenseStatus.UpdateExpenseStatusCommand(id, request.Status));
        return Ok(new { success = result });
    }
}

public record UpdateExpenseStatusRequest(string Status);
