using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using _1Rad.Application.Features.Finance.Commands.ApplyCredit;
using _1Rad.Application.Features.Finance.Commands.RefundCredit;
using _1Rad.Application.Features.Finance.Queries.GetPatientCredit;
using _1Rad.Application.Features.Finance.Queries.GetOutstandingCredits;
using _1Rad.Domain.Constants;

namespace _1RadAPI.Controllers.Finance;

/// <summary>
/// Patient credit wallet (advances / overpayments / refunds).
/// Keeps credit lifecycle separate from invoicing and payment flows.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/finance")]
[_1RadAPI.Authorization.RequiresModule(ModuleConstants.Ris)]
public class CreditController : ControllerBase
{
    private readonly IMediator _mediator;

    public CreditController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>The patient's current wallet balance + ledger.</summary>
    [HttpGet("credit/{patientId}")]
    public async Task<IActionResult> GetPatientCredit(Guid patientId)
    {
        var result = await _mediator.Send(new GetPatientCreditQuery(patientId));
        return Ok(result);
    }

    /// <summary>Everyone currently holding an advance (for the Advances & Refunds list).</summary>
    [HttpGet("credits/outstanding")]
    public async Task<IActionResult> GetOutstandingCredits()
    {
        var result = await _mediator.Send(new GetOutstandingCreditsQuery());
        return Ok(result);
    }

    /// <summary>Carry a patient's advance forward onto a later invoice.</summary>
    [HttpPost("credit/apply")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Accountant}")]
    public async Task<IActionResult> ApplyCredit([FromBody] ApplyCreditCommand command)
    {
        var result = await _mediator.Send(command);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Return a patient's advance as cash (direct — no approval).</summary>
    [HttpPost("credit/refund")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Accountant}")]
    public async Task<IActionResult> RefundCredit([FromBody] RefundCreditCommand command)
    {
        var result = await _mediator.Send(command);
        return result.Success ? Ok(result) : BadRequest(result);
    }
}
