using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using _1Rad.Application.Features.Finance.Queries.ExportFinancials;
using _1Rad.Application.Features.Finance.Queries.GetFinanceStats;
using _1Rad.Application.Features.Finance.Queries.GetFinancialMatrix;
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
    [Authorize(Roles = RoleSets.FinanceReports)]
    public async Task<IActionResult> GetStats()
    {
        var result = await _mediator.Send(new GetFinanceStatsQuery());
        return Ok(result);
    }

    [HttpGet("matrix")]
    [Authorize(Roles = RoleSets.FinanceReports)]
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
    [Authorize(Roles = RoleSets.FinanceReports)]
    public async Task<IActionResult> Export(
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate)
    {
        var result = await _mediator.Send(new ExportFinancialsQuery
        {
            StartDate = startDate,
            EndDate = endDate
        });
        var fileName = $"1RadFlow_Financials_{DateTime.Now:yyyyMMdd}.xlsx";
        return File(result, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    // Review B12: the cash view — money that actually moved, by IST day, mode and cashier.
    [HttpGet("daybook")]
    [Authorize(Roles = RoleSets.Billing)]
    public async Task<IActionResult> DayBook([FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var today = _1Rad.Application.Common.IstDateRange.ToIst(DateTime.UtcNow).Date;
        var result = await _mediator.Send(new _1Rad.Application.Features.Finance.DayBook.GetDayBookQuery(from ?? today, to ?? from ?? today));
        return Ok(result);
    }

    [HttpPost("daybook/close")]
    [Authorize(Roles = RoleSets.Billing)]
    public async Task<IActionResult> CloseDay([FromBody] CloseDayRequest body)
    {
        var result = await _mediator.Send(new _1Rad.Application.Features.Finance.DayBook.CloseDayCommand(body.Date, body.CountedCash, body.Note));
        return Ok(result);
    }

    public record CloseDayRequest(DateTime Date, decimal CountedCash, string? Note);
}
