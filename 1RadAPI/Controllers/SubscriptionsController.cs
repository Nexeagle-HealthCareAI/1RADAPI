using _1Rad.Application.Features.Subscriptions.Commands.ApprovePaymentRequest;
using _1Rad.Application.Features.Subscriptions.Commands.RejectPaymentRequest;
using _1Rad.Application.Features.Subscriptions.Commands.SetHospitalModules;
using _1Rad.Application.Features.Subscriptions.Commands.SubmitPaymentRequest;
using _1Rad.Application.Features.Subscriptions.Queries.GetSubscriptionStatus;
using _1Rad.Application.Features.Subscriptions.Queries.GetAllPaymentRequests;
using _1Rad.Application.Features.Subscriptions.Queries.GetPlans;
using _1Rad.Application.Features.Subscriptions.Queries.GetBillingEstimate;
using _1Rad.Domain.Constants;
using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/subscriptions")]
public class SubscriptionsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IConfiguration _configuration;

    public SubscriptionsController(IMediator mediator, IConfiguration configuration)
    {
        _mediator = mediator;
        _configuration = configuration;
    }

    // Gap analysis P0-2: the two platform-admin endpoints below used to need only a
    // logged-in user of ANY centre, so a centre could approve its own payment. They now
    // accept only the CMS service key (X-Service-Key == ServiceAuth:CmsServiceKey), the
    // same convention as GET hospitals/admin/all. Empty key in config = disabled.
    private bool HasValidServiceKey(string? serviceKey)
    {
        var expected = _configuration["ServiceAuth:CmsServiceKey"];
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(serviceKey)) return false;
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(serviceKey);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// Returns the current subscription status for the authenticated hospital.
    /// Returns HTTP 402 if the subscription is locked.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var result = await _mediator.Send(new GetSubscriptionStatusQuery());
        if (result.IsLocked)
            return StatusCode(402, result); // Payment Required
        return Ok(result);
    }

    /// <summary>
    /// [ADMIN ONLY] Fetch all payment requests across the platform.
    /// </summary>
    [HttpGet("admin/payment-requests")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAdminPaymentRequests([FromHeader(Name = "X-Service-Key")] string? serviceKey)
    {
        if (!HasValidServiceKey(serviceKey)) return Unauthorized();
        var result = await _mediator.Send(new GetAllPaymentRequestsQuery());
        return Ok(new { success = true, data = result });
    }

    /// <summary>
    /// Submit payment evidence for plan activation review.
    /// </summary>
    [HttpPost("payment-request")]
    public async Task<IActionResult> SubmitPaymentRequest([FromBody] SubmitPaymentRequestCommand command)
    {
        var result = await _mediator.Send(command);
        return result.Success ? Ok(result) : BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// [INTERNAL] Approve a payment request and activate the plan.
    /// Protected: only accessible by super-admin or internal API key.
    /// </summary>
    [HttpPost("approve-payment/{requestId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> ApprovePayment(Guid requestId, [FromBody] ApprovePaymentBody body,
        [FromHeader(Name = "X-Service-Key")] string? serviceKey)
    {
        if (!HasValidServiceKey(serviceKey)) return Unauthorized();
        var result = await _mediator.Send(new ApprovePaymentRequestCommand(requestId, body.ReviewNote));
        return result.Success ? Ok(result) : BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// [INTERNAL] Reject a pending payment request with a reason the centre will see.
    /// Service-key only, like approve-payment.
    /// </summary>
    [HttpPost("reject-payment/{requestId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> RejectPayment(Guid requestId, [FromBody] RejectPaymentBody body,
        [FromHeader(Name = "X-Service-Key")] string? serviceKey)
    {
        if (!HasValidServiceKey(serviceKey)) return Unauthorized();
        var result = await _mediator.Send(new RejectPaymentRequestCommand(requestId, body?.Reason ?? string.Empty));
        return result.Success ? Ok(result) : BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// Returns subscription invoices/transactions for the authenticated hospital.
    /// </summary>
    [HttpGet("invoices")]
    public async Task<IActionResult> GetInvoices()
    {
        var result = await _mediator.Send(new _1Rad.Application.Features.Subscriptions.Queries.GetSubscriptionTransactions.GetSubscriptionTransactionsQuery());
        return Ok(new { success = true, data = result });
    }

    /// <summary>
    /// Lists active subscription plans (edition × cycle) with pricing + storage.
    /// Public so the registration package-picker and a pricing page can read it.
    /// </summary>
    [HttpGet("plans")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPlans()
    {
        var result = await _mediator.Send(new GetPlansQuery());
        return Ok(new { success = true, data = result });
    }

    /// <summary>
    /// Amount due for a plan right now = base price + metered PACS storage
    /// overage from the authenticated center's current usage.
    /// </summary>
    [HttpGet("estimate")]
    public async Task<IActionResult> GetEstimate([FromQuery] Guid planId)
    {
        var result = await _mediator.Send(new GetBillingEstimateQuery { PlanId = planId });
        return result.Found
            ? Ok(new { success = true, data = result })
            : BadRequest(new { success = false, error = result.Error });
    }

    /// <summary>
    /// [ADMIN] Set the active center's product modules (RIS / PACS). Removing
    /// PACS starts the read-only downgrade grace period (after which studies are
    /// auto-deleted); re-adding it cancels the grace.
    /// </summary>
    [HttpPost("modules")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator}")]
    public async Task<IActionResult> SetModules([FromBody] SetModulesBody body)
    {
        var result = await _mediator.Send(new SetHospitalModulesCommand(body?.Modules ?? string.Empty));
        return result.Success
            ? Ok(new { success = true, data = result })
            : BadRequest(new { success = false, error = result.Error });
    }
}

public record ApprovePaymentBody(string? ReviewNote);
public record RejectPaymentBody(string? Reason);
public record SetModulesBody(string? Modules);
