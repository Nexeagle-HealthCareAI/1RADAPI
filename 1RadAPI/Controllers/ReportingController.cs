using _1Rad.Application.Features.Reporting.Commands.GenerateVoiceReport;
using _1Rad.Application.Features.Reporting.Commands.DeleteKeyword;
using _1Rad.Application.Features.Reporting.Commands.DeleteTemplate;
using _1Rad.Application.Features.Reporting.Commands.SaveReport;
using _1Rad.Application.Features.Reporting.Commands.UpsertKeyword;
using _1Rad.Application.Features.Reporting.Commands.UpsertTemplate;
using _1Rad.Application.Features.Reporting.Queries.GetKeywords;
using _1Rad.Application.Features.Reporting.Queries.GetReport;
using _1Rad.Application.Features.Reporting.Queries.GetTemplates;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers
{
    [Route("api/v1/[controller]")]
    [ApiController]
    public class ReportingController : ControllerBase
    {
        private readonly IMediator _mediator;

        public ReportingController(IMediator mediator)
        {
            _mediator = mediator;
        }

        // --- TEMPLATE QUERIES & COMMANDS ---
        
        /// <summary>
        /// Get report templates for the current hospital and doctor
        /// </summary>
        [HttpGet("templates")]
        public async Task<IActionResult> GetTemplates([FromQuery] string? modality = null)
        {
            try
            {
                var query = new GetTemplatesQuery { Modality = modality };
                var templates = await _mediator.Send(query);
                return Ok(new { success = true, data = templates });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to retrieve templates: {ex.Message}" });
            }
        }

        /// <summary>
        /// Create or update a report template
        /// </summary>
        [HttpPost("templates/upsert")]
        public async Task<IActionResult> UpsertTemplate([FromBody] UpsertTemplateCommand command)
        {
            try
            {
                var template = await _mediator.Send(command);
                return Ok(new { success = true, data = template });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { success = false, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { success = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to save template: {ex.Message}" });
            }
        }

        /// <summary>
        /// Delete a report template
        /// </summary>
        [HttpDelete("templates/{id}")]
        public async Task<IActionResult> DeleteTemplate(Guid id)
        {
            try
            {
                var command = new DeleteTemplateCommand { Id = id };
                await _mediator.Send(command);
                return Ok(new { success = true });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { success = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to delete template: {ex.Message}" });
            }
        }

        // --- KEYWORD INTELLIGENCE ---
        
        /// <summary>
        /// Get reporting keywords for the current doctor
        /// </summary>
        [HttpGet("keywords")]
        public async Task<IActionResult> GetKeywords()
        {
            try
            {
                var query = new GetKeywordsQuery();
                var keywords = await _mediator.Send(query);
                return Ok(new { success = true, data = keywords });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to retrieve keywords: {ex.Message}" });
            }
        }

        /// <summary>
        /// Create or update a reporting keyword
        /// </summary>
        [HttpPost("keywords/upsert")]
        public async Task<IActionResult> UpsertKeyword([FromBody] UpsertKeywordCommand command)
        {
            try
            {
                var keyword = await _mediator.Send(command);
                return Ok(new { success = true, data = keyword });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { success = false, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { success = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to save keyword: {ex.Message}" });
            }
        }

        /// <summary>
        /// Delete a reporting keyword
        /// </summary>
        [HttpDelete("keywords/{id}")]
        public async Task<IActionResult> DeleteKeyword(Guid id)
        {
            try
            {
                var command = new DeleteKeywordCommand { Id = id };
                await _mediator.Send(command);
                return Ok(new { success = true });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { success = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to delete keyword: {ex.Message}" });
            }
        }

        // --- REPORT PERSISTENCE ---
        
        /// <summary>
        /// Get diagnostic report by appointment ID
        /// </summary>
        [HttpGet("report/{appointmentId}")]
        public async Task<IActionResult> GetReport(string appointmentId)
        {
            try
            {
                var query = new GetReportQuery { AppointmentId = appointmentId };
                var report = await _mediator.Send(query);
                return Ok(new { success = true, data = report });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to retrieve report: {ex.Message}" });
            }
        }

        /// <summary>
        /// Save or update a diagnostic report
        /// </summary>
        [HttpPost("save")]
        public async Task<IActionResult> SaveReport([FromBody] SaveReportCommand command)
        {
            try
            {
                var report = await _mediator.Send(command);
                return Ok(new { success = true, data = report });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { success = false, error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { success = false, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { success = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to save report: {ex.Message}" });
            }
        }

        /// <summary>
        /// Voice Reporting — turn a dictation transcript into a structured
        /// report draft using Claude Haiku. Returns editor-ready HTML.
        /// </summary>
        [HttpPost("voice-generate")]
        public async Task<IActionResult> GenerateVoiceReport([FromBody] GenerateVoiceReportCommand command)
        {
            try
            {
                var result = await _mediator.Send(command);
                if (!result.Success)
                    return BadRequest(new { success = false, error = result.Error ?? "Generation failed." });
                return Ok(new { success = true, html = result.Html });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Voice report generation failed: {ex.Message}" });
            }
        }
    }
}
