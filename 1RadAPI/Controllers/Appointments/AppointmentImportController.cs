using _1Rad.Application.Features.Appointments.Commands.ImportAppointments;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers.Appointments;

/// <summary>
/// Bulk appointment import from Excel.
/// Isolated so import-specific validation and error handling can evolve
/// independently from live booking logic.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/appointments")]
[_1RadAPI.Authorization.RequiresModule(_1Rad.Domain.Constants.ModuleConstants.Ris)]
public class AppointmentImportController : ControllerBase
{
    private readonly IMediator _mediator;

    public AppointmentImportController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("import")]
    public async Task<IActionResult> Import(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest("Mission log file is missing or corrupted.");

        using var stream = file.OpenReadStream();
        var result = await _mediator.Send(new ImportAppointmentsCommand(stream));
        return Ok(result);
    }
}
