using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using _1Rad.Application.Features.Finance.Commands.CollectPayment;
using _1Rad.Domain.Constants;

namespace _1RadAPI.Controllers.Finance;

[Authorize]
[ApiController]
[Route("api/v1/finance")]
[_1RadAPI.Authorization.RequiresModule(ModuleConstants.Ris)]
public class PaymentController : ControllerBase
{
    private readonly IMediator _mediator;

    public PaymentController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("payments")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Receptionist},{RoleConstants.Accountant}")]
    public async Task<IActionResult> CollectPayment([FromBody] CollectPaymentCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { success = result });
    }

    [HttpPost("adjust")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Accountant}")]
    public async Task<IActionResult> ApplyExtraDiscount([FromBody] ApplyExtraDiscountCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(new { success = result });
    }

    [HttpPost("invoices/{id}/discount")]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Accountant}")]
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
}

public record ApplyDiscountRequest(
    decimal DiscountAmount,
    decimal? CentreDiscount = null,
    decimal? ReferrerDiscount = null,
    decimal? InstitutionalDeduction = null,
    decimal? AdditionalCharges = null,
    string? AdditionalChargesReason = null,
    List<_1Rad.Application.Features.Finance.Commands.ApplyInvoiceDiscount.ExtraChargeDetail>? ExtraCharges = null);
