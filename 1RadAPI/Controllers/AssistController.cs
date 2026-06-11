using _1Rad.Application.Features.Assist.RadAi;
using _1Rad.Application.Features.Assist.Retrain;
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

    // RadAI "retrain" insight for admins: the most-asked questions in this centre,
    // and especially the ones the knowledge base did NOT cover, each with a
    // paste-ready draft entry for app_knowledge.json.
    // NOTE: restrict to admins via your standard admin policy/attribute before
    // shipping (the class is [Authorize] but not yet admin-only).
    [HttpGet("retrain-candidates")]
    public async Task<IActionResult> RetrainCandidates([FromQuery] GetRadAiRetrainCandidatesQuery query)
    {
        var result = await _mediator.Send(query);
        return Ok(result);
    }
}
