using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
        private readonly IDicomExtractionQueue _extractionQueue;
        private readonly IDicomExtractionService _extractionService;
        private readonly IConfiguration _configuration;

        public StudyController(
            IApplicationDbContext context,
            IBlobService blobService,
            IUserContext userContext,
            IDicomExtractionQueue extractionQueue,
            IDicomExtractionService extractionService,
            IConfiguration configuration)
        {
            _context = context;
            _blobService = blobService;
            _userContext = userContext;
            _extractionQueue = extractionQueue;
            _extractionService = extractionService;
            _configuration = configuration;
        }

        /// <summary>
        /// Rewrites an Azure Blob URL to go through the CDN / Front Door
        /// endpoint configured in <c>AzureBlobStorage:CdnBaseUrl</c>
        /// (e.g. https://cdn.1rad.app or https://1rad-dicom.azurefd.net).
        ///
        /// DICOM slices are immutable and SAS-free on read, so Front Door
        /// caches them at the edge: the second-and-later access to any slice
        /// is a ~20 ms edge hit instead of a round trip to Blob in a single
        /// region. We only rewrite the host — the path (/{container}/{blob})
        /// is preserved, and Front Door's route maps it 1:1 to the origin.
        ///
        /// When the setting is empty the original Blob URL is returned
        /// unchanged, so the CDN is an opt-in toggle with zero-redeploy
        /// rollback (clear the App Setting and every URL reverts to Blob).
        /// </summary>
        private string? ToCdn(string? blobUrl)
        {
            if (string.IsNullOrEmpty(blobUrl)) return blobUrl;
            var cdnBase = _configuration["AzureBlobStorage:CdnBaseUrl"];
            if (string.IsNullOrWhiteSpace(cdnBase)) return blobUrl;
            try
            {
                var u = new Uri(blobUrl);
                // Only rewrite OUR blob hosts — leave anything else (already a
                // CDN URL, external, or relative) untouched.
                if (!u.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase))
                    return blobUrl;
                return $"{cdnBase.TrimEnd('/')}{u.AbsolutePath}";
            }
            catch
            {
                return blobUrl;
            }
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

        /// <summary>
        /// Option C manifest endpoint. Returns the per-slice URL list for an
        /// appointment, grouped by series. The viewer uses this to load slices
        /// individually instead of downloading + unzipping the whole ZIP.
        ///
        /// Lazy extraction: if an asset hasn't been extracted yet (legacy ZIPs
        /// from before this feature, or extraction still in flight), we kick
        /// off extraction synchronously for the first viewer hit so subsequent
        /// hits are instant. The response then includes a 202-style
        /// <c>extracting: true</c> flag for assets the frontend should fall
        /// back to ZIP-load for.
        /// </summary>
        [HttpGet("{appointmentId}/manifest")]
        public async Task<IActionResult> GetManifest(string appointmentId, CancellationToken cancellationToken)
        {
            Guid.TryParse(appointmentId, out var guidId);

            var appointment = await _context.Appointments
                .FirstOrDefaultAsync(a => (guidId != Guid.Empty && a.AppointmentId == guidId) || a.DisplayId == appointmentId, cancellationToken);
            if (appointment == null)
                return NotFound(new { success = false, error = "Appointment not found." });

            var assets = await _context.StudyAssets
                .Where(a => a.AppointmentId == appointment.AppointmentId)
                .OrderByDescending(a => a.UploadedAt)
                .ToListAsync(cancellationToken);

            if (assets.Count == 0)
                return Ok(new { success = true, data = new { appointmentId = appointment.AppointmentId, assets = Array.Empty<object>() } });

            // Lazy fallback: any ZIP without an ExtractionStatus row is a
            // legacy upload — extract it now (blocking the first viewer hit).
            // Statuses Queued / Running mean the worker will pick it up; we
            // don't block, just tell the frontend to fall back to ZIP for now.
            foreach (var a in assets.Where(a =>
                         (a.FileType ?? "").Equals("zip", StringComparison.OrdinalIgnoreCase) &&
                         string.IsNullOrEmpty(a.ExtractionStatus)))
            {
                try
                {
                    await _extractionService.ExtractAsync(a.Id, cancellationToken);
                }
                catch
                {
                    // ExtractAsync flagged the asset; just continue and the
                    // response will tell the frontend to use ZIP fallback.
                }
            }

            // Reload after potential lazy extraction so we see the new state.
            assets = await _context.StudyAssets
                .Where(a => a.AppointmentId == appointment.AppointmentId)
                .Include(a => a.Slices)
                .OrderByDescending(a => a.UploadedAt)
                .ToListAsync(cancellationToken);

            var assetDtos = assets.Select(a =>
            {
                if (!(a.FileType ?? "").Equals("zip", StringComparison.OrdinalIgnoreCase))
                {
                    // Pass-through for non-ZIP attachments (single DCM, JPG, PNG).
                    return new
                    {
                        assetId = a.Id,
                        appointmentServiceId = a.AppointmentServiceId,
                        fileName = a.FileName,
                        fileType = a.FileType,
                        blobUrl = ToCdn(a.BlobUrl),
                        extractionStatus = "NotApplicable",
                        series = (object?)null,
                    };
                }

                var isExtracted = a.ExtractionStatus == "Extracted" && a.Slices.Count > 0;
                if (!isExtracted)
                {
                    // Frontend should download + unzip this asset itself (legacy path).
                    return new
                    {
                        assetId = a.Id,
                        appointmentServiceId = a.AppointmentServiceId,
                        fileName = a.FileName,
                        fileType = a.FileType,
                        blobUrl = ToCdn(a.BlobUrl),
                        extractionStatus = a.ExtractionStatus ?? "Pending",
                        series = (object?)null,
                    };
                }

                var seriesGroups = a.Slices
                    .GroupBy(s => s.SeriesUID)
                    .Select(g =>
                    {
                        var first = g.OrderBy(s => s.InstanceNumber ?? int.MaxValue).First();
                        return new
                        {
                            seriesUID = g.Key,
                            seriesDescription = first.SeriesDescription,
                            modality = first.Modality,
                            thumbnailUrl = ToCdn(first.ThumbnailUrl),
                            slices = g.OrderBy(s => s.InstanceNumber ?? int.MaxValue)
                                      .ThenBy(s => s.SopInstanceUID)
                                      .Select(s => new
                                      {
                                          sopInstanceUID = s.SopInstanceUID,
                                          instanceNumber = s.InstanceNumber,
                                          url = ToCdn(s.BlobUrl),
                                          metadata = s.MetadataJson,
                                      })
                                      .ToList(),
                        };
                    })
                    .ToList();

                return new
                {
                    assetId = a.Id,
                    // Multi-service rollout — every series produced
                    // from an asset inherits the asset's owning
                    // AppointmentService so the viewer can strict-
                    // match per service rather than guessing from the
                    // DICOM-tag modality (which is ambiguous when a
                    // visit has two CT services or similar).
                    appointmentServiceId = a.AppointmentServiceId,
                    fileName = a.FileName,
                    fileType = a.FileType,
                    blobUrl = ToCdn(a.BlobUrl), // kept for fallback compat
                    extractionStatus = "Extracted",
                    series = (object?)seriesGroups,
                };
            }).ToList();

            return Ok(new
            {
                success = true,
                data = new
                {
                    appointmentId = appointment.AppointmentId,
                    patientName = appointment.PatientName,
                    modality = appointment.Modality,
                    studyDate = appointment.DateTime,
                    assets = assetDtos,
                },
            });
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
                        blobUrl = ToCdn(a.BlobUrl),
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
                    var detail = ex.InnerException != null ? $"{ex.Message} → {ex.InnerException.Message}" : ex.Message;
                    return StatusCode(500, new { success = false, error = $"AZURE STORAGE FAILURE: Could not stream clinical asset to the cloud. {detail}" });
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
                    // Multi-service rollout — adopt a freshly-supplied service id
                    // even on the upsert path so re-uploading after a booking
                    // gained services correctly attaches to the right line.
                    if (request.AppointmentServiceId.HasValue)
                        existingAsset.AppointmentServiceId = request.AppointmentServiceId;
                    asset = existingAsset;
                }
                else
                {
                    asset = new StudyAsset
                    {
                        Id = Guid.NewGuid(),
                        AppointmentId = request.AppointmentId,
                        AppointmentServiceId = request.AppointmentServiceId,
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

                // Mark for DICOM extraction (Option C). Only ZIPs need it; other
                // file types fall through with NotApplicable.
                if (asset.FileType?.Equals("zip", StringComparison.OrdinalIgnoreCase) == true)
                {
                    asset.ExtractionStatus = "Queued";
                }

                await _context.SaveChangesAsync(default);

                if (asset.ExtractionStatus == "Queued")
                    _extractionQueue.Enqueue(asset.Id);

                return Ok(new { success = true, data = asset });
            }
            catch (Exception ex)
            {
                // Surface the inner exception too — a missing column (e.g. an
                // unapplied schema migration like ExtractionStatus / AppointmentServiceId)
                // shows up there, not in the outer DbUpdateException message.
                var detail = ex.InnerException != null ? $"{ex.Message} → {ex.InnerException.Message}" : ex.Message;
                return StatusCode(500, new { success = false, error = $"INTERNAL ACQUISITION FAILURE: {detail}" });
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

        [HttpPost("upload-token")]
        public async Task<IActionResult> RequestSasUploadToken([FromBody] SasUploadTokenRequest request)
        {
            try
            {
                if (request == null || request.AppointmentId == Guid.Empty)
                    return BadRequest(new { success = false, error = "AppointmentId is required." });

                if (string.IsNullOrWhiteSpace(request.FileName))
                    return BadRequest(new { success = false, error = "FileName is required." });

                // Hard size cap — adjust if you ever need bigger studies.
                const long MaxBytes = 1_073_741_824L; // 1 GB
                if (request.FileSize > MaxBytes)
                    return BadRequest(new { success = false, error = $"File too large. Maximum allowed is {MaxBytes / (1024 * 1024)} MB." });

                var appointment = await _context.Appointments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId);

                if (appointment == null)
                    return NotFound(new { success = false, error = "Appointment not found." });

                var fileName = Path.GetFileName(request.FileName);
                // Foldered path keeps blobs organised + easy to delete by appointment.
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                var assetId = Guid.NewGuid();
                var blobPath = $"{appointment.HospitalId:N}/{appointment.AppointmentId:N}/{stamp}_{assetId:N}_{fileName}";

                SasUploadTarget target;
                try
                {
                    target = await _blobService.GenerateSasUploadUrlAsync(
                        blobPath,
                        "dicom-files",
                        TimeSpan.FromMinutes(30),
                        request.ContentType);
                }
                catch (Exception ex)
                {
                    return StatusCode(500, new { success = false, error = $"SAS_GENERATION_FAILURE: {ex.Message}" });
                }

                // NOTE: we deliberately do NOT create the StudyAsset row here. A pre-created row
                // becomes a 404-causing orphan if the PUT fails (e.g., CORS, network drop, SAS
                // expiry). The row is created in /upload-complete only after the blob is
                // confirmed to exist in Azure.
                return Ok(new
                {
                    success = true,
                    data = new
                    {
                        assetId,                              // freshly minted GUID — client sends back in /complete
                        sasUrl = target.SasUrl,
                        publicReadUrl = target.PublicReadUrl,
                        blobPath = target.BlobPath,
                        containerName = target.ContainerName,
                        expiresAt = target.ExpiresAt,
                    },
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"UPLOAD_TOKEN_FAILURE: {ex.Message}" });
            }
        }

        [HttpPost("upload-complete")]
        public async Task<IActionResult> ConfirmSasUploadComplete([FromBody] SasUploadCompleteRequest request)
        {
            try
            {
                if (request == null
                    || request.AssetId == Guid.Empty
                    || request.AppointmentId == Guid.Empty
                    || string.IsNullOrWhiteSpace(request.BlobPath)
                    || string.IsNullOrWhiteSpace(request.ContainerName)
                    || string.IsNullOrWhiteSpace(request.PublicReadUrl)
                    || string.IsNullOrWhiteSpace(request.FileName))
                {
                    return BadRequest(new { success = false, error = "AssetId, AppointmentId, BlobPath, ContainerName, PublicReadUrl and FileName are all required." });
                }

                var appointment = await _context.Appointments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId);
                if (appointment == null)
                    return NotFound(new { success = false, error = "Appointment not found." });

                // Verify the blob actually exists in Azure before we write any DB row.
                var exists = await _blobService.BlobExistsAsync(request.BlobPath, request.ContainerName);
                if (!exists)
                {
                    return BadRequest(new
                    {
                        success = false,
                        error = "Blob not found in Azure. The PUT may have failed (most commonly: CORS not yet configured on the storage account) or the SAS expired before upload finished.",
                    });
                }

                var fileName = Path.GetFileName(request.FileName);
                var extension = Path.GetExtension(fileName).ToLower().TrimStart('.');

                // Idempotent: if this AssetId was already inserted (retry / double-tap),
                // just bump UploadedAt instead of duplicating.
                var asset = await _context.StudyAssets
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.Id == request.AssetId);
                if (asset == null)
                {
                    asset = new StudyAsset
                    {
                        Id = request.AssetId,
                        AppointmentId = request.AppointmentId,
                        AppointmentServiceId = request.AppointmentServiceId,
                        BlobUrl = request.PublicReadUrl,
                        FileName = fileName,
                        FileType = string.IsNullOrEmpty(extension) ? "bin" : extension,
                        UploadedAt = DateTime.UtcNow,
                        HospitalId = appointment.HospitalId,
                    };
                    _context.StudyAssets.Add(asset);
                }
                else
                {
                    asset.BlobUrl = request.PublicReadUrl;
                    asset.UploadedAt = DateTime.UtcNow;
                    if (request.AppointmentServiceId.HasValue)
                        asset.AppointmentServiceId = request.AppointmentServiceId;
                }

                if (appointment.Status != "SCANNED" && appointment.Status != "REPORTED")
                {
                    appointment.Status = "IN_PROGRESS";
                }

                // Mark for DICOM extraction (Option C). Only ZIPs need extraction.
                if (asset.FileType?.Equals("zip", StringComparison.OrdinalIgnoreCase) == true)
                {
                    asset.ExtractionStatus = "Queued";
                }

                await _context.SaveChangesAsync(default);

                if (asset.ExtractionStatus == "Queued")
                    _extractionQueue.Enqueue(asset.Id);

                return Ok(new { success = true, data = asset });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"UPLOAD_COMPLETE_FAILURE: {ex.Message}" });
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

                // Security check: only proxy our own storage. Accept any of our
                // accounts (dev "1radstorage", prod "1radstorageprod", …) — the
                // old check hardcoded "1radstorage.blob.core.windows.net", which
                // does NOT match "1radstorageprod.blob…", so the proxy rejected
                // every production asset as "Unauthorized asset origin".
                if (!url.Contains("1radstorage") || !url.Contains(".blob.core.windows.net"))
                    return BadRequest("Unauthorized asset origin");

                // Use Uri to strip query parameters for extension checking
                var cleanPath = new Uri(url).AbsolutePath;
                var contentType = cleanPath.ToLower().EndsWith(".zip") ? "application/zip" : 
                                  cleanPath.ToLower().EndsWith(".pdf") ? "application/pdf" : 
                                  cleanPath.ToLower().EndsWith(".png") ? "image/png" :
                                  cleanPath.ToLower().EndsWith(".jpg") || cleanPath.ToLower().EndsWith(".jpeg") ? "image/jpeg" :
                                  "application/octet-stream";

                if (Request.Method == "HEAD")
                {
                    // For HEAD requests, we don't want to download the whole blob
                    // We just want to confirm it exists and get its metadata if possible
                    return Ok(); 
                }

                var stream = await _blobService.DownloadFileAsync(url);
                return File(stream, contentType, Path.GetFileName(cleanPath), true); // enableRangeProcessing: true for large files
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
        // Multi-service rollout (step 7). Optional — when supplied, the
        // resulting StudyAsset is stamped against this specific service
        // line so a multi-modality visit's CT images attach to the CT
        // service (and so the right report opens against the right
        // images). NULL = legacy behaviour, asset only tracks the parent
        // appointment.
        public Guid? AppointmentServiceId { get; set; }
        public IFormFile File { get; set; }
    }

    public class StudyCompletionRequest
    {
        public Guid AppointmentId { get; set; }
        public string Comments { get; set; }
    }

    public class SasUploadTokenRequest
    {
        public Guid AppointmentId { get; set; }
        // Multi-service rollout — when the technician's workspace is
        // scoped to a specific AppointmentService line, the client
        // forwards the FK here so the eventual /upload-complete call
        // can stamp it on StudyAsset. We don't persist anything at
        // token time (no DB row created yet) but accepting the field
        // keeps model binding clean and avoids "unknown field" warnings.
        public Guid? AppointmentServiceId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string? ContentType { get; set; }
    }

    public class SasUploadCompleteRequest
    {
        public Guid AssetId { get; set; }
        public Guid AppointmentId { get; set; }
        // Multi-service rollout (step 7) — see StudyUploadRequest.AppointmentServiceId.
        public Guid? AppointmentServiceId { get; set; }
        public string BlobPath { get; set; } = string.Empty;
        public string ContainerName { get; set; } = string.Empty;
        public string PublicReadUrl { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long? ActualSize { get; set; } // optional integrity check hint
    }
}
