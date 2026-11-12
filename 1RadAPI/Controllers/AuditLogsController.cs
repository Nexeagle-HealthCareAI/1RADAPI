using _1Rad.Application.Features.Audit.Queries;
using _1Rad.Domain.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

/// <summary>Read-only audit trail for the current centre (gap analysis P3-8).</summary>
[ApiController]
[Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator}")]
[Route("api/v1/audit-logs")]
public class AuditLogsController : ControllerBase
{
    private readonly IMediator _mediator;

    public AuditLogsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? entityType, [FromQuery] string? entityId, [FromQuery] Guid? userId,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var result = await _mediator.Send(new GetAuditLogsQuery(
            entityType, entityId, userId,
            from?.ToUniversalTime(), to?.ToUniversalTime(), page, pageSize));
        return Ok(new { success = true, data = result });
    }
}
