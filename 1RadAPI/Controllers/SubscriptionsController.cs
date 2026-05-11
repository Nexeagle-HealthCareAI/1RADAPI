using _1Rad.Application.Features.Subscriptions.Queries.GetSubscriptionStatus;
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

    [HttpGet("status")]
    public async Task<ActionResult<SubscriptionStatusResponse>> GetStatus()
    {
        return await _mediator.Send(new GetSubscriptionStatusQuery());
    }
}
