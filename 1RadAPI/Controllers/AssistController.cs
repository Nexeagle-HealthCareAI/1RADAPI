using _1Rad.Application.Features.Assist.RadAi;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

// RadAI in-app help desk. Authenticated staff only; the AI key stays server-side.
[Authorize]
[ApiController]
[Route("api/v1/assist")]
public class AssistController : ControllerBase
{
    private readonly IMediator _mediator;

    public AssistController(IMediator mediator) => _mediator = mediator;

    [HttpPost("radai")]
    public async Task<IActionResult> RadAi([FromBody] RadAiCommand command)
    {
        var result = await _mediator.Send(command);
        return Ok(result);
    }
}
