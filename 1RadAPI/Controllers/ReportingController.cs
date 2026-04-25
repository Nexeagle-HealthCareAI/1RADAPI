using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace _1RadAPI.Controllers
{
    [Route("api/v1/[controller]")]
    [ApiController]
    public class ReportingController : ControllerBase
    {
        private readonly IApplicationDbContext _context;
        private readonly IUserContext _userContext;

        public ReportingController(IApplicationDbContext context, IUserContext userContext)
        {
            _context = context;
            _userContext = userContext;
        }

        // --- TEMPLATE COMMANDS ---
        [HttpGet("templates")]
        public async Task<IActionResult> GetTemplates([FromQuery] string modality = null)
        {
            var hospitalId = _userContext.HospitalId;
            var doctorId = _userContext.UserId;

            var query = _context.ReportTemplates
                .Where(t => t.HospitalId == hospitalId && (t.DoctorId == null || t.DoctorId == doctorId));

            if (!string.IsNullOrEmpty(modality) && modality != "ALL")
            {
                query = query.Where(t => t.Modality == modality);
            }

            var templates = await query.ToListAsync();
            return Ok(new { success = true, data = templates });
        }

        [HttpPost("templates/upsert")]
        public async Task<IActionResult> UpsertTemplate([FromBody] ReportTemplate template)
        {
            var hospitalId = _userContext.HospitalId;
            var doctorId = _userContext.UserId;

            var existing = await _context.ReportTemplates
                .FirstOrDefaultAsync(t => t.Id == template.Id);

            if (existing == null)
            {
                template.HospitalId = hospitalId;
                template.DoctorId = doctorId;
                _context.ReportTemplates.Add(template);
            }
            else
            {
                existing.Name = template.Name;
                existing.Modality = template.Modality;
                existing.Content = template.Content;
                existing.IsStructured = template.IsStructured;
            }

            await _context.SaveChangesAsync(default);
            return Ok(new { success = true, data = template });
        }

        [HttpDelete("templates/{id}")]
        public async Task<IActionResult> DeleteTemplate(Guid id)
        {
            var hospitalId = _userContext.HospitalId;
            var doctorId = _userContext.UserId;

            var template = await _context.ReportTemplates
                .FirstOrDefaultAsync(t => t.Id == id && t.HospitalId == hospitalId && (t.DoctorId == null || t.DoctorId == doctorId));

            if (template == null) return NotFound(new { success = false, error = "ACCESS_DENIED: Template not found or unauthorized." });

            _context.ReportTemplates.Remove(template);
            await _context.SaveChangesAsync(default);
            return Ok(new { success = true });
        }

        // --- KEYWORD INTELLIGENCE ---
        [HttpGet("keywords")]
        public async Task<IActionResult> GetKeywords()
        {
            var doctorId = _userContext.UserId;
            var keywords = await _context.ReportingKeywords
                .Where(k => k.DoctorId == doctorId)
                .ToListAsync();

            return Ok(new { success = true, data = keywords });
        }

        [HttpPost("keywords/upsert")]
        public async Task<IActionResult> UpsertKeyword([FromBody] ReportingKeyword keyword)
        {
            var hospitalId = _userContext.HospitalId;
            var doctorId = _userContext.UserId;

            var existing = await _context.ReportingKeywords
                .FirstOrDefaultAsync(k => k.Id == keyword.Id);

            if (existing == null)
            {
                keyword.HospitalId = hospitalId;
                keyword.DoctorId = doctorId;
                _context.ReportingKeywords.Add(keyword);
            }
            else
            {
                existing.Trigger = keyword.Trigger;
                existing.ReplacementText = keyword.ReplacementText;
            }

            await _context.SaveChangesAsync(default);
            return Ok(new { success = true, data = keyword });
        }

        [HttpDelete("keywords/{id}")]
        public async Task<IActionResult> DeleteKeyword(Guid id)
        {
            var doctorId = _userContext.UserId;
            var keyword = await _context.ReportingKeywords
                .FirstOrDefaultAsync(k => k.Id == id && k.DoctorId == doctorId);

            if (keyword == null) return NotFound(new { success = false, error = "ACCESS_DENIED: Keyword not found or unauthorized." });

            _context.ReportingKeywords.Remove(keyword);
            await _context.SaveChangesAsync(default);
            return Ok(new { success = true });
        }

        // --- REPORT PERSISTENCE ---
        [HttpGet("report/{appointmentId}")]
        public async Task<IActionResult> GetReport(string appointmentId)
        {
            Guid.TryParse(appointmentId, out var guidId);
            
            var report = await _context.DiagnosticReports
                .Include(r => r.Appointment)
                .FirstOrDefaultAsync(r => (guidId != Guid.Empty && r.AppointmentId == guidId) || r.Appointment.DisplayId == appointmentId);

            return Ok(new { success = true, data = report });
        }

        [HttpPost("save")]
        public async Task<IActionResult> SaveReport([FromBody] ReportRequest request)
        {
            try
            {
                var hospitalId = _userContext.HospitalId;
                var doctorId = _userContext.UserId;

                _ = Guid.TryParse(request.AppointmentId, out var guidId);
                
                // Tactical: Always fetch the appointment to ensure correct context (HospitalId)
                var appointment = await _context.Appointments
                    .FirstOrDefaultAsync(a => a.AppointmentId == guidId || a.DisplayId == request.AppointmentId);
                
                if (appointment == null) 
                    return BadRequest(new { success = false, error = "VALIDATION FAILURE: The target mission for this report does not exist." });

                var report = await _context.DiagnosticReports
                    .FirstOrDefaultAsync(r => r.AppointmentId == appointment.AppointmentId);

                if (report == null)
                {
                    report = new DiagnosticReport
                    {
                        Id = Guid.NewGuid(),
                        AppointmentId = appointment.AppointmentId,
                        DoctorId = doctorId,
                        HospitalId = appointment.HospitalId, // Inherit from appointment to prevent FK conflict
                        TemplateId = request.TemplateId,
                        Findings = request.Findings,
                        Impression = request.Impression,
                        Advice = request.Advice,
                        IsFinalized = request.IsFinalized,
                        FinalizedAt = request.IsFinalized ? DateTime.UtcNow : null,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.DiagnosticReports.Add(report);
                }
                else
                {
                    report.TemplateId = request.TemplateId;
                    report.Findings = request.Findings;
                    report.Impression = request.Impression;
                    report.Advice = request.Advice;
                    report.IsFinalized = request.IsFinalized;
                    report.FinalizedAt = request.IsFinalized ? DateTime.UtcNow : report.FinalizedAt;
                }

                // If finalized, update the appointment status
                if (request.IsFinalized && appointment != null)
                {
                    appointment.Status = "REPORTED";
                }

                await _context.SaveChangesAsync(default);

                return Ok(new { success = true, data = report });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"REPORT PERSISTENCE FAILURE: {ex.Message}" });
            }
        }
    }

    public class ReportRequest
    {
        public string AppointmentId { get; set; }
        public Guid? TemplateId { get; set; }
        public string Findings { get; set; }
        public string Impression { get; set; }
        public string Advice { get; set; }
        public bool IsFinalized { get; set; }
    }
}
