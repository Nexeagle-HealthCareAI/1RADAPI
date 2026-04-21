using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using _1Rad.Application.Features.Finance.Queries.GetServiceCharges;
using _1Rad.Application.Features.Finance.Queries.GetInvoices;
using _1Rad.Application.Features.Finance.Queries.GetFinanceStats;
using _1Rad.Application.Features.Finance.Commands.UpsertServiceCharge;
using _1Rad.Application.Features.Finance.Commands.DeleteServiceCharge;
using _1Rad.Application.Features.Finance.Commands.GenerateInvoice;
using _1Rad.Application.Features.Finance.Commands.CollectPayment;
using _1Rad.Application.Features.Finance.Commands.SyncLocalStorageInvoices;
using _1Rad.Application.Features.Finance.Queries.ExportFinancials;
using _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix;
using _1Rad.Application.Features.Finance.Queries.GetPendingBillables;
using _1Rad.Application.Features.Finance.Commands.RecordExpense;

using _1Rad.Application.Features.Finance.Queries.GetExpenses;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/finance")]
public class FinanceController : ControllerBase
{
    private readonly IMediator _mediator;

    public FinanceController(IMediator mediator)
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
    public async Task<IActionResult> UpsertRegistry([FromBody] UpsertServiceChargeCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { id = result });
    }

    [HttpDelete("registry/{id}")]
    public async Task<IActionResult> DeleteRegistry(Guid id)
    {
        await _mediator.Send(new DeleteServiceChargeCommand(id));
        return Ok();
    }

    [HttpGet("invoices")]
    public async Task<IActionResult> GetInvoices([FromQuery] string? search, [FromQuery] string? status, [FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate)
    {
        var result = await _mediator.Send(new GetInvoicesQuery { Search = search, Status = status, StartDate = startDate, EndDate = endDate });
        return Ok(result);
    }

    [HttpPost("invoices")]
    public async Task<IActionResult> GenerateInvoice([FromBody] GenerateInvoiceCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { invoiceId = result });
    }

    [HttpPost("payments")]
    public async Task<IActionResult> CollectPayment([FromBody] CollectPaymentCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { success = result });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var result = await _mediator.Send(new GetFinanceStatsQuery());
        return Ok(result);
    }

    [HttpPost("sync")]
    public async Task<IActionResult> SyncLegacy([FromBody] SyncLocalStorageInvoicesCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { syncedCount = result });
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate)
    {
        var result = await _mediator.Send(new ExportFinancialsQuery { StartDate = startDate, EndDate = endDate });
        var fileName = $"1Rad_Financials_{DateTime.Now:yyyyMMdd}.xlsx";
        return File(result, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    [HttpGet("matrix")]
    public async Task<IActionResult> GetMatrix()
    {
        var result = await _mediator.Send(new GetFinancialMatrixQuery());
        return Ok(result);
    }

    [HttpGet("pending-billables/{patientId}")]
    public async Task<IActionResult> GetPendingBillables(Guid patientId)
    {
        var result = await _mediator.Send(new GetPendingBillablesQuery { PatientId = patientId });
        return Ok(result);
    }

    [HttpGet("expenses")]
    public async Task<IActionResult> GetExpenses([FromQuery] string? search, [FromQuery] string? category, [FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate)
    {
        var result = await _mediator.Send(new GetExpensesQuery { Search = search, Category = category, StartDate = startDate, EndDate = endDate });
        return Ok(result);
    }

    [HttpPost("expense")]
    public async Task<IActionResult> RecordExpense([FromBody] RecordExpenseCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { id = result });
    }
}
