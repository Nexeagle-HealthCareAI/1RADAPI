using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using _1Rad.Application.Features.Finance.Commands.GenerateInvoice;
using _1Rad.Application.Features.Finance.Queries.GetInvoices;
using _1Rad.Application.Features.Finance.Queries.GetPendingBillables;
using _1Rad.Domain.Constants;

namespace _1RadAPI.Controllers.Finance;

[Authorize]
[ApiController]
[Route("api/v1/finance")]
[_1RadAPI.Authorization.RequiresModule(ModuleConstants.Ris)]
public class InvoiceController : ControllerBase
{
    private readonly IMediator _mediator;

    public InvoiceController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("invoices")]
    public async Task<IActionResult> GetInvoices(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] DateTime? updatedAfter,
        [FromQuery] Guid? appointmentId,
        [FromQuery] bool includeDeleted = false,
        [FromQuery] int pageSize = 0,
        [FromQuery] string? cursor = null)
    {
        var result = await _mediator.Send(new GetInvoicesQuery
        {
            Search = search,
            Status = status,
            StartDate = startDate,
            EndDate = endDate,
            UpdatedAfter = updatedAfter,
            AppointmentId = appointmentId,
            IncludeDeleted = includeDeleted,
            PageSize = pageSize,
            Cursor = cursor,
        });

        // Backwards-compat: the sync engine and legacy callers that do NOT
        // send pageSize expect a plain JSON array, not a paged envelope.
        if (!result.IsPaged)
            return Ok(result.Items);

        return Ok(result);
    }

    [HttpPost("invoices")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Receptionist},{RoleConstants.Accountant}")]
    public async Task<IActionResult> GenerateInvoice([FromBody] GenerateInvoiceCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { invoiceId = result });
    }

    [HttpDelete("invoices/{id}")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Accountant}")]
    public async Task<IActionResult> DeleteInvoice(Guid id, [FromQuery] Guid? commissionId = null)
    {
        var (success, error) = await _mediator.Send(
            new _1Rad.Application.Features.Finance.Commands.DeleteInvoice.DeleteInvoiceCommand(id, commissionId));
        return success
            ? Ok()
            : BadRequest(new { message = error });
    }

    [HttpGet("pending-billables/{patientId}")]
    public async Task<IActionResult> GetPendingBillables(Guid patientId)
    {
        var result = await _mediator.Send(new GetPendingBillablesQuery { PatientId = patientId });
        return Ok(result);
    }
}
