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
                if (request.IsFinalized)
                {
                    _ = Guid.TryParse(request.AppointmentId, out var finalGuid);
                    var appointment = await _context.Appointments
                        .FirstOrDefaultAsync(a => a.AppointmentId == finalGuid || a.DisplayId == request.AppointmentId);
                    if (appointment != null)
                    {
                        appointment.Status = "REPORTED";
                    }
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
