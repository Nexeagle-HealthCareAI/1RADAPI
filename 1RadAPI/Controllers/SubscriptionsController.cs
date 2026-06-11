using _1Rad.Application.Features.Subscriptions.Commands.ApprovePaymentRequest;
using _1Rad.Application.Features.Subscriptions.Commands.SetHospitalModules;
using _1Rad.Application.Features.Subscriptions.Commands.SubmitPaymentRequest;
using _1Rad.Application.Features.Subscriptions.Queries.GetSubscriptionStatus;
using _1Rad.Application.Features.Subscriptions.Queries.GetAllPaymentRequests;
using _1Rad.Application.Features.Subscriptions.Queries.GetPlans;
using _1Rad.Application.Features.Subscriptions.Queries.GetBillingEstimate;
using _1Rad.Domain.Constants;
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

    public SubscriptionsController(IMediator mediator)
    {
        _mediator = mediator;
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
    public async Task<IActionResult> GetAdminPaymentRequests()
    {
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
    public async Task<IActionResult> ApprovePayment(Guid requestId, [FromBody] ApprovePaymentBody body)
    {
        var result = await _mediator.Send(new ApprovePaymentRequestCommand(requestId, body.ReviewNote));
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
public record SetModulesBody(string? Modules);
