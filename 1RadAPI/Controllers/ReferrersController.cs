using _1Rad.Application.Features.Referrers.Queries.GetReferrers;
using _1Rad.Application.Features.Referrers.Queries.GetPatientSourceBreakdown;
using _1Rad.Application.Features.Referrers.Queries.GetReferralIntelligence;
using _1Rad.Application.Features.Referrers.Queries.GetReferralMatrix;
using _1Rad.Application.Features.Referrers.Queries.GetReferralCommissions;
using _1Rad.Application.Features.Referrers.Queries.GetDetailedReferralLedger;
using _1Rad.Application.Features.Referrers.Commands.CreateReferrer;
using _1Rad.Application.Features.Referrers.Commands.CreateReferrersBulk;
using _1Rad.Application.Features.Referrers.Commands.SendReferralLinks;
using _1Rad.Application.Features.Referrers.Commands.SendReferralLinksWhatsApp;
using _1Rad.Application.Features.Referrers.Commands.UpdateReferrer;
using _1Rad.Application.Interfaces;
using _1Rad.Application.Features.Referrers.Commands.DeleteReferrer;
using _1Rad.Application.Features.Referrers.Commands.RecordReferralCommission;
using _1Rad.Application.Features.Referrers.Commands.RecordReferralCommissions;
using _1Rad.Application.Features.Referrers.Commands.PayReferralCommissions;
using _1Rad.Application.Features.Referrers.Commands.RevokeReferralLinks;
using _1Rad.Application.Features.Referrers.Queries.GetReferralLinkStatus;
using _1Rad.Application.Common;
using _1Rad.Application.Features.Referrers.Commands.WriteOffReferralDeficit;
using _1Rad.Application.Features.Referrers.Commands.UpdateReferralCommission;
using _1Rad.Application.Features.Referrers.Commands.UpdateReferralCommissionStatus;
using _1Rad.Application.Features.Referrers.Commands.MergeReferrers;
using _1Rad.Application.Features.Referrers.Commands.UnmergeReferrer;
using _1Rad.Application.Features.Referrers.Commands.DeclineBookingRequest;
using _1Rad.Application.Features.Referrers.Queries.GetBookingRequests;
using _1Rad.Domain.Constants;
using MediatR;
using Microsoft.EntityFrameworkCore;
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
    private readonly IApplicationDbContext _context;

    public ReferrersController(IMediator mediator, IReferralLinkTokenService referralTokens, IApplicationDbContext context)
    {
        _mediator = mediator;
        _referralTokens = referralTokens;
        _context = context;
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
    // A link is only ever minted for a live partner of the CALLER's centre — the token
    // is a bearer credential for that partner's earnings, so signing one for an
    // arbitrary id (another centre's partner, a deleted one) is refused.
    [HttpGet("{id:guid}/share-link")]
    public async Task<IActionResult> ShareLink(Guid id)
    {
        var hospitalId = _context.UserContext.HospitalId;
        var exists = await _context.Referrers.AnyAsync(r => r.ReferrerId == id && r.HospitalId == hospitalId && r.DeletedAt == null);
        if (!exists) return NotFound(new { success = false, error = "Partner not found." });
        var version = await ReferralLinkVersions.GetOneAsync(_context, id, HttpContext.RequestAborted);
        return Ok(new { success = true, referrerId = id, token = _referralTokens.Issue(id, version) });
    }

    // Mint tokens for several referrers at once (bulk copy / WhatsApp).
    public sealed record ShareLinksBody(List<Guid> ReferrerIds);
    [HttpPost("share-links")]
    public async Task<IActionResult> ShareLinks([FromBody] ShareLinksBody body)
    {
        var requested = (body?.ReferrerIds ?? new List<Guid>()).Distinct().ToList();
        var hospitalId = _context.UserContext.HospitalId;
        var allowed = await _context.Referrers
            .Where(r => requested.Contains(r.ReferrerId) && r.HospitalId == hospitalId && r.DeletedAt == null)
            .Select(r => r.ReferrerId)
            .ToListAsync();
        var versions = await ReferralLinkVersions.GetAsync(_context, allowed, HttpContext.RequestAborted);
        var links = allowed.Select(id => new { referrerId = id, token = _referralTokens.Issue(id, versions.GetValueOrDefault(id)) });
        return Ok(new { success = true, links });
    }

    // Per-partner link state for the Doctor Links tab: when a link was last sent, over
    // which channel, when it expires, and whether it renews automatically.
    [HttpGet("link-status")]
    public async Task<IActionResult> GetLinkStatus()
    {
        var result = await _mediator.Send(new GetReferralLinkStatusQuery());
        return Ok(result);
    }

    // Pull back every portal link ever issued for this partner (and any partner merged
    // into them): a forwarded message, a lost phone, a doctor who left. Old links stop
    // working immediately; links minted afterwards (copy / email / WhatsApp) work.
    [HttpPost("{id:guid}/revoke-links")]
    public async Task<IActionResult> RevokeLinks(Guid id)
    {
        var result = await _mediator.Send(new RevokeReferralLinksCommand(id));
        return Ok(result);
    }

    // Email each named referrer their personal portal link.
    [HttpPost("send-links")]
    public async Task<IActionResult> SendLinks([FromBody] SendReferralLinksCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(result);
    }

    // WhatsApp each named referrer their personal portal link via NexEagle's
    // WhatsApp Business API (one-click send, no app hand-off).
    [HttpPost("send-links-whatsapp")]
    public async Task<IActionResult> SendLinksWhatsApp([FromBody] SendReferralLinksWhatsAppCommand command)
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

    /// <summary>
    /// Source Analytics summary: one row per source with every total (visits, money, new vs
    /// returning, modality mix) and NO visit rows. Open a source with <c>intelligence/visits</c>.
    /// </summary>
    [HttpGet("intelligence/summary")]
    public async Task<IActionResult> GetIntelligenceSummary([FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate)
    {
        var result = await _mediator.Send(new GetReferralIntelligenceQuery(startDate, endDate, SummaryOnly: true));
        return Ok(result);
    }

    /// <summary>
    /// One source's visits, newest first, a page at a time. Returns that source's row (totals cover
    /// every visit) with <c>patients</c> holding just this page. A source with nothing in the range comes back as an empty row.
    /// </summary>
    [HttpGet("intelligence/visits")]
    public async Task<IActionResult> GetIntelligenceVisits(
        [FromQuery] string sourceKey,
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return BadRequest(new { success = false, message = "sourceKey is required." });
        // A page is capped so one call can never pull back a whole year of one partner's visits.
        take = Math.Clamp(take, 1, 500);
        skip = Math.Max(0, skip);
        var result = await _mediator.Send(new GetReferralIntelligenceQuery(startDate, endDate, SourceKey: sourceKey, Skip: skip, Take: take));
        var node = result.FirstOrDefault(n => string.Equals(n.SourceKey, sourceKey.Trim(), StringComparison.OrdinalIgnoreCase));
        return Ok(node ?? new ReferrerIntelligenceDto(Guid.Empty, string.Empty, string.Empty, string.Empty, 0, new List<ReferredPatientDto>(), SourceKey: sourceKey.Trim()));
    }

    /// <summary>How patients heard about the centre, totalled by channel (attended visits, IST days).</summary>
    [HttpGet("acquisition-sources")]
    public async Task<IActionResult> GetAcquisitionSources([FromQuery] DateTime? startDate, [FromQuery] DateTime? endDate)
    {
        var result = await _mediator.Send(new GetPatientSourceBreakdownQuery(startDate, endDate));
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
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Accountant}")]
    public async Task<IActionResult> RecordCommission([FromBody] RecordReferralCommissionCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { commissionId = result });
    }

    [HttpPost("commissions/batch")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Accountant}")]
    public async Task<IActionResult> RecordCommissions([FromBody] RecordReferralCommissionsCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { commissionIds = result });
    }

    // Settle several commissions in ONE transaction with one set of disbursement
    // details. Replaces the browser firing one PATCH per row (a dropped
    // connection left the payout half-recorded). Rows it won't pay come back in
    // `skipped` with a reason; re-submitting is safe (already-paid rows skip).
    [HttpPost("commissions/pay")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Accountant}")]
    public async Task<IActionResult> PayCommissions([FromBody] PayReferralCommissionsCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(result);
    }

    // The centre absorbs a partner's outstanding clawback deficit (settles the open
    // negative rows and books the compensating write-off). The amount is computed
    // server-side from live rows; a repeat call is refused because nothing is open.
    [HttpPost("{id:guid}/write-off-deficit")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Accountant}")]
    public async Task<IActionResult> WriteOffDeficit(Guid id)
    {
        var result = await _mediator.Send(new WriteOffReferralDeficitCommand(id));
        return Ok(result);
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
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Accountant}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateReferralCommissionCommand command)
    {
        if (id != command.CommissionId) return BadRequest("Identity mismatch.");
        var result = await _mediator.Send(command);
        return Ok(result);
    }

    [HttpPatch("commissions/{id}/status")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Accountant}")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] CommissionStatusUpdateDto dto)
    {
        var result = await _mediator.Send(new UpdateReferralCommissionStatusCommand(
            id, dto.Status,
            dto.PaidBy, dto.PayeeName, dto.PayeeContact,
            dto.PayeeEmail, dto.PayeeAddress, dto.UpdatedBy));
        return Ok(result);
    }

    [HttpPost("merge")]
    public async Task<IActionResult> Merge([FromBody] MergeReferrersCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(result);
    }

    [HttpPost("{id}/unmerge")]
    public async Task<IActionResult> Unmerge(Guid id)
    {
        var result = await _mediator.Send(new UnmergeReferrerCommand(id));
        return Ok(result);
    }

    // ── Doctor-portal booking requests (front desk side) ────────────────────────
    // A referring doctor's booking request, submitted from their portal link. See
    // PublicReferralController for the doctor-facing submit/list endpoints, and
    // ReferralBookingRequest's doc comment for why this isn't just a plain Appointment.

    [HttpGet("booking-requests")]
    public async Task<IActionResult> GetBookingRequests([FromQuery] bool includeDecided = true)
    {
        var result = await _mediator.Send(new GetBookingRequestsQuery(includeDecided));
        return Ok(result);
    }

    [HttpPost("booking-requests/{id:guid}/decline")]
    public async Task<IActionResult> DeclineBookingRequest(Guid id, [FromBody] DeclineBookingRequestBody? body)
    {
        await _mediator.Send(new DeclineBookingRequestCommand(id, body?.Reason));
        return Ok(new { success = true });
    }

    public sealed record DeclineBookingRequestBody(string? Reason);
}

/// <summary>Request body for PATCH /commissions/{id}/status.</summary>
public record CommissionStatusUpdateDto(
    string Status,
    string? PaidBy = null,
    string? PayeeName = null,
    string? PayeeContact = null,
    string? PayeeEmail = null,
    string? PayeeAddress = null,
    string? UpdatedBy = null
);
