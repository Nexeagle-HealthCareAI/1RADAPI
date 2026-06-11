using _1Rad.Application.Features.Referrers.Queries.GetReferrers;
using _1Rad.Application.Features.Referrers.Queries.GetReferralIntelligence;
using _1Rad.Application.Features.Referrers.Queries.GetReferralMatrix;
using _1Rad.Application.Features.Referrers.Queries.GetReferralCommissions;
using _1Rad.Application.Features.Referrers.Queries.GetDetailedReferralLedger;
using _1Rad.Application.Features.Referrers.Commands.CreateReferrer;
using _1Rad.Application.Features.Referrers.Commands.CreateReferrersBulk;
using _1Rad.Application.Features.Referrers.Commands.SendReferralLinks;
using _1Rad.Application.Features.Referrers.Commands.UpdateReferrer;
using _1Rad.Application.Interfaces;
using _1Rad.Application.Features.Referrers.Commands.DeleteReferrer;
using _1Rad.Application.Features.Referrers.Commands.RecordReferralCommission;
using _1Rad.Application.Features.Referrers.Commands.RecordReferralCommissions;
using _1Rad.Application.Features.Referrers.Commands.UpdateReferralCommission;
using _1Rad.Application.Features.Referrers.Commands.UpdateReferralCommissionStatus;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/referrers")]
[_1RadAPI.Authorization.RequiresModule(_1Rad.Domain.Constants.ModuleConstants.Ris)]
public class ReferrersController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IReferralLinkTokenService _referralTokens;

    public ReferrersController(IMediator mediator, IReferralLinkTokenService referralTokens)
    {
        _mediator = mediator;
        _referralTokens = referralTokens;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? search,
        [FromQuery] DateTime? updatedAfter,
        [FromQuery] bool includeDeleted = false)
    {
        var result = await _mediator.Send(new GetReferrersQuery(search, updatedAfter, includeDeleted));
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateReferrerCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { referrerId = result });
    }

    // Bulk-add partners from the inline multi-add grid or an Excel upload.
    [HttpPost("bulk")]
    public async Task<IActionResult> CreateBulk([FromBody] CreateReferrersBulkCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(result);
    }

    // ── Doctor-portal share links (#3) ─────────────────────────────────────
    // Mint this referrer's signed portal-link token (for copy / WhatsApp).
    [HttpGet("{id:guid}/share-link")]
    public IActionResult ShareLink(Guid id)
        => Ok(new { success = true, referrerId = id, token = _referralTokens.Issue(id) });

    // Mint tokens for several referrers at once (bulk copy / WhatsApp).
    public sealed record ShareLinksBody(List<Guid> ReferrerIds);
    [HttpPost("share-links")]
    public IActionResult ShareLinks([FromBody] ShareLinksBody body)
    {
        var links = (body?.ReferrerIds ?? new List<Guid>()).Distinct()
            .Select(id => new { referrerId = id, token = _referralTokens.Issue(id) });
        return Ok(new { success = true, links });
    }

    // Email each named referrer their personal portal link.
    [HttpPost("send-links")]
    public async Task<IActionResult> SendLinks([FromBody] SendReferralLinksCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(result);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateReferrer(Guid id, [FromBody] UpdateReferrerCommand command)
    {
        if (id != command.ReferrerId) return BadRequest("Identity mismatch.");
        var result = await _mediator.Send(command);
        return Ok(result);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteReferrer(Guid id)
    {
        var result = await _mediator.Send(new DeleteReferrerCommand(id));
        if (!result) return NotFound(new { success = false, error = "Partner not found or already removed." });
        return Ok(new { success = true });
    }

    [HttpGet("intelligence")]
    public async Task<IActionResult> GetIntelligence([FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate, [FromQuery] Guid? referrerId)
    {
        var result = await _mediator.Send(new GetReferralIntelligenceQuery(startDate, endDate, referrerId));
        return Ok(result);
    }

    [HttpGet("matrix")]
    public async Task<IActionResult> GetMatrix(
        [FromQuery] string period, 
        [FromQuery] DateTime referenceDate, 
        [FromQuery] int weekIndex = 1,
        [FromQuery] string? search = null)
    {
        var result = await _mediator.Send(new GetReferralMatrixQuery(period, referenceDate, weekIndex, search));
        return Ok(result);
    }

    [HttpPost("commissions")]
    public async Task<IActionResult> RecordCommission([FromBody] RecordReferralCommissionCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { commissionId = result });
    }

    [HttpPost("commissions/batch")]
    public async Task<IActionResult> RecordCommissions([FromBody] RecordReferralCommissionsCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { commissionIds = result });
    }

    [HttpGet("commissions")]
    public async Task<IActionResult> GetCommissions(
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] Guid? referrerId,
        [FromQuery] DateTime? updatedAfter,
        [FromQuery] bool includeDeleted = false)
    {
        var result = await _mediator.Send(new GetReferralCommissionsQuery(startDate, endDate, referrerId, updatedAfter, includeDeleted));
        return Ok(result);
    }

    [HttpGet("ledger")]
    public async Task<IActionResult> GetLedger([FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate, [FromQuery] Guid? referrerId)
    {
        var result = await _mediator.Send(new GetDetailedReferralLedgerQuery(startDate, endDate, referrerId));
        return Ok(result);
    }

    [HttpPut("commissions/{id}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateReferralCommissionCommand command)
    {
        if (id != command.CommissionId) return BadRequest("Identity mismatch.");
        var result = await _mediator.Send(command);
        return Ok(result);
    }

    [HttpPatch("commissions/{id}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] string status)
    {
        var result = await _mediator.Send(new UpdateReferralCommissionStatusCommand(id, status));
        return Ok(result);
    }
}
