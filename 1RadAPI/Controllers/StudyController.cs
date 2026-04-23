using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace _1RadAPI.Controllers
{
    [Route("api/v1/[controller]")]
    [ApiController]
    public class StudyController : ControllerBase
    {
        private readonly IApplicationDbContext _context;
        private readonly IBlobService _blobService;
        private readonly IUserContext _userContext;

        public StudyController(IApplicationDbContext context, IBlobService blobService, IUserContext userContext)
        {
            _context = context;
            _blobService = blobService;
            _userContext = userContext;
        }

        [HttpGet("{appointmentId}/assets")]
        public async Task<IActionResult> GetStudyAssets(string appointmentId)
        {
            Guid.TryParse(appointmentId, out var guidId);

            var assets = await _context.StudyAssets
                .Where(a => (guidId != Guid.Empty && a.AppointmentId == guidId) || a.Appointment.DisplayId == appointmentId)
                .OrderByDescending(a => a.UploadedAt)
                .ToListAsync();

            return Ok(assets);
        }

        [HttpPost("upload")]
        [DisableRequestSizeLimit]
        public async Task<IActionResult> UploadStudyAsset([FromForm] StudyUploadRequest request)
        {
            try
            {
                if (request == null)
                    return BadRequest(new { success = false, error = "PROTOCOL FAILURE: Null request payload detected." });

                if (request.File == null || request.File.Length == 0)
                    return BadRequest(new { success = false, error = "PROTOCOL FAILURE: No clinical binary stream detected." });

                using var stream = request.File.OpenReadStream();
                var fileName = request.File.FileName;
                var contentType = request.File.ContentType;
                var extension = Path.GetExtension(fileName).ToLower();

                // Tactical Upload to diagnostic-studies container
                string blobUrl;
                try
                {
                    blobUrl = await _blobService.UploadFileAsync(stream, fileName, contentType);
                }
                catch (Exception ex)
                {
                    return StatusCode(500, new { success = false, error = $"AZURE STORAGE FAILURE: Could not stream clinical asset to the cloud. {ex.Message}" });
                }

                // Tactical: Check for existing asset to prevent duplicates (Upsert logic)
                var existingAsset = await _context.StudyAssets
                    .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId && a.FileName == fileName);

                if (existingAsset != null)
                {
                    existingAsset.BlobUrl = blobUrl;
                    existingAsset.FileType = extension.Replace(".", "");
                    existingAsset.UploadedAt = DateTime.UtcNow;
                }
                else
                {
                    var asset = new StudyAsset
                    {
                        Id = Guid.NewGuid(),
                        AppointmentId = request.AppointmentId,
                        BlobUrl = blobUrl,
                        FileName = fileName,
                        FileType = extension.Replace(".", ""),
                        UploadedAt = DateTime.UtcNow,
                        HospitalId = _userContext.HospitalId
                    };
                    _context.StudyAssets.Add(asset);
                }
                
                // Auto-update status to IN_PROGRESS if first asset
                var appointment = await _context.Appointments.FindAsync(request.AppointmentId);
                if (appointment != null && appointment.Status != "SCANNED")
                {
                    appointment.Status = "IN_PROGRESS";
                }

                await _context.SaveChangesAsync(default);

                return Ok(new { success = true, data = asset });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"INTERNAL ACQUISITION FAILURE: {ex.Message}" });
            }
        }

        [HttpPost("complete")]
        public async Task<IActionResult> CompleteStudy([FromBody] StudyCompletionRequest request)
        {
            try
            {
                var appointment = await _context.Appointments
                    .Include(a => a.StudyAssets)
                    .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId);

                if (appointment == null) return NotFound(new { success = false, error = "MISSION NOT FOUND: The specified appointment does not exist." });

                appointment.TechnicianComments = request.Comments;
                appointment.Status = "SCANNED"; // MARK AS READY FOR RADIOLOGIST
                appointment.ScannedAt = DateTime.UtcNow;
                appointment.TechnicianId = _userContext.UserId;

                await _context.SaveChangesAsync(default);

                return Ok(new { success = true, status = "READY_FOR_REPORT", comments = request.Comments });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"MISSION FINALIZATION FAILURE: {ex.Message}" });
            }
        }
    }

    public class StudyUploadRequest
    {
        public Guid AppointmentId { get; set; }
        public IFormFile File { get; set; }
    }

    public class StudyCompletionRequest
    {
        public Guid AppointmentId { get; set; }
        public string Comments { get; set; }
    }
}
