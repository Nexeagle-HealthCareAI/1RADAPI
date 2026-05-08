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
    [Microsoft.AspNetCore.Authorization.Authorize]
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

        [HttpGet("{appointmentId}/viewer")]
        public async Task<IActionResult> GetDicomViewerConfig(string appointmentId)
        {
            try
            {
                Guid.TryParse(appointmentId, out var guidId);

                var assets = await _context.StudyAssets
                    .Where(a => (guidId != Guid.Empty && a.AppointmentId == guidId) || a.Appointment.DisplayId == appointmentId)
                    .OrderByDescending(a => a.UploadedAt)
                    .ToListAsync();

                var appointment = await _context.Appointments
                    .FirstOrDefaultAsync(a => (guidId != Guid.Empty && a.AppointmentId == guidId) || a.DisplayId == appointmentId);

                if (appointment == null)
                    return NotFound(new { success = false, error = "Appointment not found" });

                // Detect device type from User-Agent
                var userAgent = Request.Headers.UserAgent.ToString();
                var deviceInfo = GetDeviceInfo(userAgent);

                var viewerConfig = new
                {
                    appointmentId = appointment.AppointmentId,
                    patientName = appointment.PatientName,
                    modality = appointment.Modality,
                    studyDate = appointment.DateTime,
                    assets = assets.Select(a => new
                    {
                        id = a.Id,
                        fileName = a.FileName,
                        fileType = a.FileType,
                        blobUrl = a.BlobUrl,
                        uploadedAt = a.UploadedAt
                    }),
                    deviceInfo = deviceInfo,
                    mobileOptimizations = GetMobileOptimizations(deviceInfo)
                };

                return Ok(new { success = true, data = viewerConfig });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to get viewer configuration: {ex.Message}" });
            }
        }

        private object GetDeviceInfo(string userAgent)
        {
            var isMobile = userAgent.Contains("Mobile");
            var isTablet = userAgent.Contains("iPad") || 
                          (userAgent.Contains("Android") && !userAgent.Contains("Mobile")) ||
                          userAgent.Contains("Tablet");
            var isIOS = userAgent.Contains("iPhone") || userAgent.Contains("iPad") || userAgent.Contains("iPod");
            var isAndroid = userAgent.Contains("Android");
            var isSafari = userAgent.Contains("Safari") && !userAgent.Contains("Chrome");

            return new
            {
                isMobile = isMobile,
                isTablet = isTablet,
                isIOS = isIOS,
                isAndroid = isAndroid,
                isSafari = isSafari,
                userAgent = userAgent
            };
        }

        private object GetMobileOptimizations(object deviceInfo)
        {
            // Cast deviceInfo to access properties
            var device = deviceInfo as dynamic ?? new { isTablet = false, isIOS = false, isMobile = false };
            
            return new
            {
                // Memory settings based on device
                maxCacheSize = device.isTablet ? "512MB" : "256MB",
                preloadImages = device.isTablet ? 5 : 3,
                
                // Rendering settings
                webGLEnabled = true,
                pixelReplication = false,
                interpolation = "linear",
                
                // Touch settings
                touchGestures = new
                {
                    pan = true,
                    zoom = true,
                    windowLevel = true,
                    rotate = device.isTablet
                },
                
                // Performance settings
                maxConcurrentRequests = device.isMobile ? 2 : 4,
                networkTimeout = 30000,
                
                // iOS-specific settings
                safariOptimizations = device.isIOS ? new
                {
                    preventElasticScroll = true,
                    disableUserSelect = true,
                    touchAction = "none"
                } : null
            };
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

                // Tactical Upload to 'dicom-files' container for clinical isolation
                string blobUrl;
                try
                {
                    blobUrl = await _blobService.UploadFileAsync(stream, fileName, contentType, "dicom-files");
                }
                catch (Exception ex)
                {
                    return StatusCode(500, new { success = false, error = $"AZURE STORAGE FAILURE: Could not stream clinical asset to the cloud. {ex.Message}" });
                }

                // Tactical: Verify appointment existence and retrieve HospitalId
                // Use IgnoreQueryFilters to ensure acquisition works regardless of session context
                var appointment = await _context.Appointments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId);

                if (appointment == null)
                    return NotFound(new { success = false, error = "MISSION NOT FOUND: Target appointment does not exist." });

                // Tactical: Check for existing asset to prevent duplicates (Upsert logic)
                var existingAsset = await _context.StudyAssets
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId && a.FileName == fileName);

                StudyAsset asset;
                if (existingAsset != null)
                {
                    existingAsset.BlobUrl = blobUrl;
                    existingAsset.FileType = extension.Replace(".", "");
                    existingAsset.UploadedAt = DateTime.UtcNow;
                    asset = existingAsset;
                }
                else
                {
                    asset = new StudyAsset
                    {
                        Id = Guid.NewGuid(),
                        AppointmentId = request.AppointmentId,
                        BlobUrl = blobUrl,
                        FileName = fileName,
                        FileType = extension.Replace(".", ""),
                        UploadedAt = DateTime.UtcNow,
                        HospitalId = appointment.HospitalId // Inherit from appointment to prevent FK conflict
                    };
                    _context.StudyAssets.Add(asset);
                }
                
                // Auto-update status to IN_PROGRESS if first asset
                if (appointment.Status != "SCANNED" && appointment.Status != "REPORTED")
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

        [HttpGet("proxy-asset")]
        [HttpHead("proxy-asset")]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public async Task<IActionResult> ProxyAsset([FromQuery] string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return BadRequest("URL is required");
                
                // Security check: ensure the URL is from 1radstorage
                if (!url.Contains("1radstorage.blob.core.windows.net"))
                    return BadRequest("Unauthorized asset origin");

                var stream = await _blobService.DownloadFileAsync(url);
                var contentType = url.ToLower().EndsWith(".zip") ? "application/zip" : 
                                  url.ToLower().EndsWith(".pdf") ? "application/pdf" : 
                                  "application/octet-stream";
                                  
                return File(stream, contentType, Path.GetFileName(new Uri(url).LocalPath));
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Proxy failure: {ex.Message}");
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
