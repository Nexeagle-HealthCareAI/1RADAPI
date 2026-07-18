using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using _1Rad.Application.Features.Finance.Queries.GetServiceCharges;
using _1Rad.Application.Features.Finance.Queries.GetInvoices;
using _1Rad.Application.Features.Finance.Queries.GetFinanceStats;
using _1Rad.Application.Features.Finance.Commands.UpsertServiceCharge;
using _1Rad.Application.Features.Finance.Commands.AddBookingService;
using _1Rad.Application.Features.Finance.Commands.DeleteServiceCharge;
using _1Rad.Domain.Constants;
using _1Rad.Application.Features.Finance.Commands.GenerateInvoice;
using _1Rad.Application.Features.Finance.Commands.CollectPayment;
using _1Rad.Application.Features.Finance.Commands.SyncLocalStorageInvoices;
using _1Rad.Application.Features.Finance.Queries.ExportFinancials;
using _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix;
using _1Rad.Application.Features.Finance.Queries.GetPendingBillables;
using _1Rad.Application.Features.Finance.Commands.RecordExpense;
using _1Rad.Application.Features.Finance.Commands.UpdateExpense;
using _1Rad.Application.Features.Finance.Commands.RefundCredit;
using _1Rad.Application.Features.Finance.Commands.ApplyCredit;
using _1Rad.Application.Features.Finance.Queries.GetPatientCredit;
using _1Rad.Application.Features.Finance.Queries.GetOutstandingCredits;

using _1Rad.Application.Features.Finance.Queries.GetExpenses;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/finance")]
[_1RadAPI.Authorization.RequiresModule(_1Rad.Domain.Constants.ModuleConstants.Ris)]
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

    [HttpPut("registry/{id}")]
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
    public async Task<IActionResult> DeleteRegistry(Guid id)
    {
        await _mediator.Send(new DeleteServiceChargeCommand(id));
        return Ok();
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
        // Return the raw list when the query wasn't paged.
        if (!result.IsPaged)
            return Ok(result.Items);

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

    // ── Patient credit wallet (advances / overpayments / refunds) ───────────
    // The patient's current wallet balance + ledger.
    [HttpGet("credit/{patientId}")]
    public async Task<IActionResult> GetPatientCredit(Guid patientId)
    {
        var result = await _mediator.Send(new GetPatientCreditQuery(patientId));
        return Ok(result);
    }

    // Everyone currently holding an advance (for the Advances & refunds list).
    [HttpGet("credits/outstanding")]
    public async Task<IActionResult> GetOutstandingCredits()
    {
        var result = await _mediator.Send(new GetOutstandingCreditsQuery());
        return Ok(result);
    }

    // Carry a patient's advance forward onto a later invoice.
    [HttpPost("credit/apply")]
    public async Task<IActionResult> ApplyCredit([FromBody] ApplyCreditCommand command)
    {
        var result = await _mediator.Send(command);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    // Return a patient's advance as cash (direct — no approval).
    [HttpPost("credit/refund")]
    public async Task<IActionResult> RefundCredit([FromBody] RefundCreditCommand command)
    {
        var result = await _mediator.Send(command);
        return result.Success ? Ok(result) : BadRequest(result);
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
    public async Task<IActionResult> GetMatrix([FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate)
    {
        var result = await _mediator.Send(new GetFinancialMatrixQuery { StartDate = startDate, EndDate = endDate });
        return Ok(result);
    }

    [HttpGet("pending-billables/{patientId}")]
    public async Task<IActionResult> GetPendingBillables(Guid patientId)
    {
        var result = await _mediator.Send(new GetPendingBillablesQuery { PatientId = patientId });
        return Ok(result);
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
    public async Task<IActionResult> RecordExpense([FromBody] RecordExpenseCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { id = result });
    }

    [HttpPut("expenses/{id}")]
    public async Task<IActionResult> UpdateExpense(Guid id, [FromBody] UpdateExpenseCommand command)
    {
        var updated = command with { Id = id };
        var result = await _mediator.Send(updated);
        return result ? Ok() : NotFound();
    }

    [HttpDelete("invoices/{id}")]
    public async Task<IActionResult> DeleteInvoice(Guid id, [FromQuery] Guid? commissionId = null)
    {
        var (success, error) = await _mediator.Send(new _1Rad.Application.Features.Finance.Commands.DeleteInvoice.DeleteInvoiceCommand(id, commissionId));
        return success 
            ? Ok() 
            : BadRequest(new { message = error });
    }

    [HttpDelete("expenses/{id}")]
    public async Task<IActionResult> DeleteExpense(Guid id)
    {
        await _mediator.Send(new _1Rad.Application.Features.Finance.Commands.DeleteExpense.DeleteExpenseCommand(id));
        return Ok();
    }

    [HttpPost("adjust")]
    public async Task<IActionResult> ApplyExtraDiscount([FromBody] ApplyExtraDiscountCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { success = result });
    }

    [HttpPost("invoices/{id}/discount")]

    public async Task<IActionResult> ApplyDiscount(Guid id, [FromBody] ApplyDiscountRequest request)
    {
        var result = await _mediator.Send(new _1Rad.Application.Features.Finance.Commands.ApplyInvoiceDiscount.ApplyInvoiceDiscountCommand
        {
            InvoiceId = id,
            DiscountAmount = request.DiscountAmount,
            CentreDiscount = request.CentreDiscount,
            ReferrerDiscount = request.ReferrerDiscount,
            InstitutionalDeduction = request.InstitutionalDeduction,
            AdditionalCharges = request.AdditionalCharges,
            AdditionalChargesReason = request.AdditionalChargesReason,
            ExtraCharges = request.ExtraCharges
        });
        return Ok(result);
    }

    [HttpPut("expenses/{id}/status")]
    public async Task<IActionResult> UpdateExpenseStatus(Guid id, [FromBody] UpdateExpenseStatusRequest request)
    {
        var result = await _mediator.Send(new _1Rad.Application.Features.Finance.Commands.UpdateExpenseStatus.UpdateExpenseStatusCommand(id, request.Status));
        return Ok(new { success = result });
    }
}

public record UpdateExpenseStatusRequest(string Status);
public record ApplyDiscountRequest(decimal DiscountAmount, decimal? CentreDiscount = null, decimal? ReferrerDiscount = null, decimal? InstitutionalDeduction = null, decimal? AdditionalCharges = null, string? AdditionalChargesReason = null, List<_1Rad.Application.Features.Finance.Commands.ApplyInvoiceDiscount.ExtraChargeDetail>? ExtraCharges = null);

