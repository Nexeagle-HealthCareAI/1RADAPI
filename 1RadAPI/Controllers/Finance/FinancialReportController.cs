using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using _1Rad.Application.Features.Finance.Queries.ExportFinancials;
using _1Rad.Application.Features.Finance.Queries.GetFinanceStats;
using _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix;
using _1Rad.Application.Features.Finance.Commands.SyncLocalStorageInvoices;
using _1Rad.Domain.Constants;

namespace _1RadAPI.Controllers.Finance;

/// <summary>
/// Financial reporting, analytics, and legacy data migration.
/// Read-heavy endpoints that aggregate across the finance domain.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/finance")]
[_1RadAPI.Authorization.RequiresModule(ModuleConstants.Ris)]
public class FinancialReportController : ControllerBase
{
    private readonly IMediator _mediator;

    public FinancialReportController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var result = await _mediator.Send(new GetFinanceStatsQuery());
        return Ok(result);
    }

    [HttpGet("matrix")]
    public async Task<IActionResult> GetMatrix(
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate)
    {
        var result = await _mediator.Send(new GetFinancialMatrixQuery
        {
            StartDate = startDate,
            EndDate = endDate
        });
        return Ok(result);
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate)
    {
        var result = await _mediator.Send(new ExportFinancialsQuery
        {
            StartDate = startDate,
            EndDate = endDate
        });
        var fileName = $"1Rad_Financials_{DateTime.Now:yyyyMMdd}.xlsx";
        return File(result, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    /// <summary>
    /// Legacy localStorage sync — kept isolated so it can be deprecated
    /// independently without touching any other financial controller.
    /// </summary>
    [HttpPost("sync")]
    public async Task<IActionResult> SyncLegacy([FromBody] SyncLocalStorageInvoicesCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { syncedCount = result });
    }
}
