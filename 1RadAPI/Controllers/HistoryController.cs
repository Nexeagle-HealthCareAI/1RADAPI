using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using _1Rad.Application.Features.Audit.Queries;
using _1Rad.Domain.Constants;

namespace _1RadAPI.Controllers;

/// <summary>
/// "Who changed what" on one bill, visit or partner, in plain words, from the audit
/// trail. Billing staff (front desk, accountant, owners) can read it for their centre.
/// </summary>
[Authorize(Roles = RoleSets.Billing)]
[ApiController]
[Route("api/v1/history")]
public class HistoryController : ControllerBase
{
    private readonly IMediator _mediator;
    public HistoryController(IMediator mediator) => _mediator = mediator;

    [HttpGet("{kind}/{id:guid}")]
    public async Task<IActionResult> Get(string kind, Guid id) => Ok(await _mediator.Send(new GetRecordHistoryQuery(kind, id)));
}
