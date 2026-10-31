using _1Rad.Application.Features.Referrers.Commands.RenewReferralLinks;
using _1Rad.Application.Features.Referrers.Commands.UpdateDoctorProfile;
using _1Rad.Application.Features.Referrers.Commands.SubmitReferralBookingRequest;
using _1Rad.Application.Features.Referrers.Queries.GetDoctorPortal;
using _1Rad.Application.Features.Referrers.Queries.GetPublicServiceMenu;
using _1Rad.Application.Features.Referrers.Queries.GetDoctorBookingRequests;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using _1RadAPI.Authorization;
using System;

namespace _1RadAPI.Controllers;

// Doctor-facing portal endpoint for the /r/{id} link. No auth — access is the
// signed capability token bound to the referrer id (mirrors PublicTracking).
[ApiController]
[AllowAnonymous]
[Route("api/v1/public/referral")]
public class PublicReferralController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IReferralLinkTokenService _tokenService;
    private readonly IApplicationDbContext _context;

    public PublicReferralController(IMediator mediator, IReferralLinkTokenService tokenService, IApplicationDbContext context)
    {
        _mediator = mediator;
        _tokenService = tokenService;
        _context = context;
    }

    // Review R23: the link token travels in the X-Referral-Token header (the portal keeps it
    // in the URL fragment, which browsers never send), so it stays out of server and
    // proxy logs and Referer headers. ?token= still works for portals opened from an
    // older link until they refresh.
    private string? LinkToken(string? queryToken)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Cache-Control"] = "no-store";
        var header = Request.Headers["X-Referral-Token"].ToString();
        return string.IsNullOrWhiteSpace(header) ? queryToken : header.Trim();
    }

    // Review R9/R23: when and how a partner's link is used, for the centre to see.
    private async Task LogViewAsync(Guid referrerId, string action, string? token)
    {
        try
        {
            var hospitalId = await _context.Referrers.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.ReferrerId == referrerId).Select(r => r.HospitalId).FirstOrDefaultAsync(HttpContext.RequestAborted);
            if (hospitalId == Guid.Empty) return;
            var version = token != null && _tokenService.TryReadClaims(token, out var claims) ? claims.Version : 0;
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            var ua = Request.Headers.UserAgent.ToString();
            _context.ReferralPortalViews.Add(new _1Rad.Domain.Entities.ReferralPortalView
            {
                HospitalId = hospitalId,
                ReferrerId = referrerId,
                Action = action,
                LinkVersion = version,
                IpHash = ip == null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"{hospitalId}:{ip}")))[..32],
                UserAgent = string.IsNullOrWhiteSpace(ua) ? null : (ua.Length > 300 ? ua[..300] : ua),
            });
            await _context.SaveChangesAsync(HttpContext.RequestAborted);
        }
        catch (Exception)
        {
            // Logging a view must never stop the doctor seeing their portal.
        }
    }

    // Signature + expiry + the partner's CURRENT link version (a revoked link fails
    // here even though its signature and expiry are still good).
    private Task<ReferralLinkState> LinkStateAsync(string token, Guid referrerId)
        => ReferralLinkAccess.CheckAsync(_context, _tokenService, token, referrerId, HttpContext.RequestAborted);

    // 401 body for a link that is not usable. A merely EXPIRED link is flagged renewable so
    // the portal can offer "send me a new link" (which goes to the contact on file).
    private IActionResult LinkRejected(ReferralLinkState state) => state == ReferralLinkState.Expired
        ? Unauthorized(new
        {
            success = false,
            code = "LINK_EXPIRED",
            canRenew = true,
            error = "Your link has expired. Tap below and we will send a fresh one to the WhatsApp number or email the diagnostic centre has for you.",
        })
        : Unauthorized(new
        {
            success = false,
            code = "LINK_INVALID",
            canRenew = false,
            error = "This link is no longer valid - it has been replaced. Please ask the diagnostic centre to send you a fresh link.",
        });

    [HttpGet("{referrerId:guid}")]
    public async Task<IActionResult> Get(Guid referrerId, [FromQuery(Name = "token")] string? queryToken)
    {
        var token = LinkToken(queryToken);
        if (string.IsNullOrWhiteSpace(token))
            return Unauthorized(new { success = false, error = "Missing link token." });
        var state = await LinkStateAsync(token, referrerId);
        if (state != ReferralLinkState.Valid) return LinkRejected(state);

        var result = await _mediator.Send(new GetDoctorPortalQuery(referrerId));
        if (result == null) return NotFound(new { success = false, error = "Referrer not found." });
        await LogViewAsync(referrerId, "OPEN", token);

        return Ok(new { success = true, data = result });
    }

    // An EXPIRED link asks for a fresh one. The new link is sent only to the WhatsApp number /
    // email the centre already has for this doctor (never to an address in the request), and
    // the request carries no portal URL - see RenewReferralLinkSelfServeCommand.
    [DistributedRateLimit("portal-renew", 20, 3600, Route = "referrerId")]
    [HttpPost("{referrerId:guid}/renew")]
    public async Task<IActionResult> Renew(Guid referrerId, [FromQuery(Name = "token")] string? queryToken)
    {
        var token = LinkToken(queryToken);
        var result = await _mediator.Send(new RenewReferralLinkSelfServeCommand(referrerId, token));
        await LogViewAsync(referrerId, "RENEW", token);
        return Ok(new { success = true, channel = result.Channel, maskedTo = result.MaskedTo });
    }

    // The doctor updates their own profile (location / specialty / degree) from
    // the portal. Same capability-token gate as the read — the token is bound to
    // this referrer id, so a doctor can only ever edit their own record.
    public sealed record UpdateProfileBody(string? Name, string? Location, string? Specialty, string? Degree, string? Email, string? Contact);

    [HttpPut("{referrerId:guid}/profile")]
    public async Task<IActionResult> UpdateProfile(Guid referrerId, [FromQuery(Name = "token")] string? queryToken, [FromBody] UpdateProfileBody body)
    {
        var token = LinkToken(queryToken);
        if (string.IsNullOrWhiteSpace(token))
            return Unauthorized(new { success = false, error = "Missing link token." });
        var state = await LinkStateAsync(token, referrerId);
        if (state != ReferralLinkState.Valid) return LinkRejected(state);

        var result = await _mediator.Send(new UpdateDoctorProfileCommand(referrerId, body?.Name, body?.Location, body?.Specialty, body?.Degree, body?.Email, body?.Contact));
        if (result.Found) await LogViewAsync(referrerId, "PROFILE", token);
        if (!result.Found) return NotFound(new { success = false, error = "Referrer not found." });

        // contactChangePending: a new mobile/email waits for the centre to confirm (review R8).
        return Ok(new { success = true, contactChangePending = result.ContactChangePending });
    }

    // ── Book from the portal ─────────────────────────────────────────────────
    // A patient the referring doctor wants booked. This does NOT create a real
    // appointment (booking one needs a Lead Specialist the centre assigns) - it
    // queues a request the front desk reviews. See ReferralBookingRequest.

    [HttpGet("{referrerId:guid}/services")]
    public async Task<IActionResult> GetServiceMenu(Guid referrerId, [FromQuery(Name = "token")] string? queryToken)
    {
        var token = LinkToken(queryToken);
        if (string.IsNullOrWhiteSpace(token))
            return Unauthorized(new { success = false, error = "Missing link token." });
        var state = await LinkStateAsync(token, referrerId);
        if (state != ReferralLinkState.Valid) return LinkRejected(state);

        var result = await _mediator.Send(new GetPublicServiceMenuQuery(referrerId));
        return Ok(new { success = true, data = result });
    }

    public sealed record BookingRequestBody(
        string PatientName, string? Mobile, string? Age, string? Gender,
        string? Modality, string? ServiceName, DateTime? PreferredDate, string? Notes);

    [DistributedRateLimit("portal-booking", 20, 3600, Route = "referrerId")]
    [HttpPost("{referrerId:guid}/booking-requests")]
    public async Task<IActionResult> SubmitBookingRequest(Guid referrerId, [FromQuery(Name = "token")] string? queryToken, [FromBody] BookingRequestBody body)
    {
        var token = LinkToken(queryToken);
        if (string.IsNullOrWhiteSpace(token))
            return Unauthorized(new { success = false, error = "Missing link token." });
        var state = await LinkStateAsync(token, referrerId);
        if (state != ReferralLinkState.Valid) return LinkRejected(state);

        var id = await _mediator.Send(new SubmitReferralBookingRequestCommand(
            referrerId, body?.PatientName ?? string.Empty, body?.Mobile, body?.Age, body?.Gender,
            body?.Modality, body?.ServiceName, body?.PreferredDate, body?.Notes));
        await LogViewAsync(referrerId, "BOOKING", token);

        return Ok(new { success = true, id });
    }

    [HttpGet("{referrerId:guid}/booking-requests")]
    public async Task<IActionResult> GetMyBookingRequests(Guid referrerId, [FromQuery(Name = "token")] string? queryToken)
    {
        var token = LinkToken(queryToken);
        if (string.IsNullOrWhiteSpace(token))
            return Unauthorized(new { success = false, error = "Missing link token." });
        var state = await LinkStateAsync(token, referrerId);
        if (state != ReferralLinkState.Valid) return LinkRejected(state);

        var result = await _mediator.Send(new GetDoctorBookingRequestsQuery(referrerId));
        return Ok(new { success = true, data = result });
    }
}
