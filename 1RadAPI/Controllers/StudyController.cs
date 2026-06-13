using _1Rad.Application.Interfaces;
using _1Rad.Domain.Constants;
using _1Rad.Domain.Entities;
using _1RadAPI.Authorization;
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
    // Module gating is per-ACTION here, not class-level: RIS-only centers may
    // attach and view non-DICOM documents (PDF/JPG) on a visit, so the generic
    // upload/asset endpoints stay open and enforce a file-type rule instead
    // (RequireDicomCapabilityAsync), while the DICOM-only surfaces (manifest,
    // viewer, per-instance upload) carry [RequiresModule(PACS)].
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
        private readonly IModuleEntitlementService _modules;
        private readonly IStorageMeteringService _storage;
        private readonly IAssetUrlSigner _signer;
        private readonly IStudyMatchingService _matching;
        private readonly IStudyShareTokenService _shareTokens;

        public StudyController(
            IApplicationDbContext context,
            IBlobService blobService,
            IUserContext userContext,
            IDicomExtractionQueue extractionQueue,
            IDicomExtractionService extractionService,
            IConfiguration configuration,
            IModuleEntitlementService modules,
            IStorageMeteringService storage,
            IAssetUrlSigner signer,
            IStudyMatchingService matching,
            IStudyShareTokenService shareTokens)
        {
            _context = context;
            _blobService = blobService;
            _userContext = userContext;
            _extractionQueue = extractionQueue;
            _extractionService = extractionService;
            _configuration = configuration;
            _modules = modules;
            _storage = storage;
            _signer = signer;
            _matching = matching;
            _shareTokens = shareTokens;
        }

        // Container holding PHI (DICOM). Reads of it require a capability: a
        // valid signature OR an authenticated, tenant-entitled Bearer.
        private const string PhiContainer = "dicom-files";

        // Containers whose blobs are intentionally public (clinic branding shown
        // on the unauthenticated patient-tracking page) and may be proxied with
        // no credentials. NOT PHI.
        private static readonly string[] OpenProxyContainers = { "prescriptions" };

        // How long a signed read URL stays valid. Long enough to outlast a
        // reporting/viewing session (the manifest is re-fetched per page load,
        // minting fresh signatures), short enough that a leaked URL expires.
        private TimeSpan SignedUrlTtl =>
            TimeSpan.FromHours(_configuration.GetValue("AssetProxy:SignedUrlTtlHours", 8));

        // Wraps a dicom-files blob URL as a signed, absolute proxy URL the
        // browser can fetch with no Bearer (the signature is the capability;
        // see <see cref="IAssetUrlSigner"/>).
        //
        // NOTE: currently UNUSED. The read seams emit ToCdn() Front Door URLs
        // (Posture A — security via the storage firewall that locks the blob to
        // Front Door). This helper + the proxy's signature branch are retained
        // as the ready-made hook for Posture B (Front Door token auth): point it
        // at the CDN host instead of the API and align the HMAC with Front Door.
        // See docs/PACS_PRIVATE_CONTAINER_RUNBOOK.md.
        private string? SignedProxyUrl(string? blobUrl)
        {
            if (string.IsNullOrEmpty(blobUrl)) return blobUrl;
            var (exp, sig) = _signer.Sign(blobUrl, SignedUrlTtl);
            var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
            return $"{baseUrl}/api/v1/Study/proxy-asset" +
                   $"?url={Uri.EscapeDataString(blobUrl)}&exp={exp}&sig={Uri.EscapeDataString(sig)}";
        }

        // First path segment of a blob URL = the container name.
        private static string ContainerOf(string url)
        {
            try
            {
                var segs = new Uri(url).AbsolutePath.TrimStart('/').Split('/', 2);
                return segs.Length > 0 ? segs[0] : string.Empty;
            }
            catch { return string.Empty; }
        }

        // True if a present Bearer belongs to a user entitled to the blob's
        // hospital. dicom-files blob paths are "/{container}/{hospitalId:N}/...",
        // so the hospital is the segment after the container.
        private bool IsBearerEntitledToBlob(string url)
        {
            if (User?.Identity?.IsAuthenticated != true) return false;
            try
            {
                var segs = new Uri(url).AbsolutePath.TrimStart('/').Split('/');
                if (segs.Length < 2 || !Guid.TryParse(segs[1], out var hid)) return false;
                return hid == _userContext.HospitalId || _userContext.AuthorizedHospitalIds.Contains(hid);
            }
            catch { return false; }
        }

        // File extensions that imply DICOM content and therefore the PACS
        // module: raw instances and whole-study ZIPs. PDF/JPG/PNG documents
        // are allowed in every SKU (RIS-only "attach report scan" flow).
        private static readonly string[] DicomExtensions = { ".zip", ".dcm", ".dicom" };

        // Imaging (vs. visit-document) file types — these get an ImagingStudy
        // aggregate row. "instances" is the per-instance bridge upload.
        private static bool IsImagingFileType(string? fileType)
        {
            var t = (fileType ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
            return t is "zip" or "dcm" or "dicom" or "instances";
        }

        // Storage quota gate (Phase 3): over-quota centers can't ingest NEW
        // DICOM — returns the 403 to send, or null when there's headroom.
        // Viewing existing studies is never blocked by quota.
        private async Task<IActionResult?> RequireStorageHeadroomAsync(Guid hospitalId)
        {
            if (!await _storage.IsOverQuotaAsync(hospitalId)) return null;
            var usage = await _storage.GetUsageAsync(hospitalId);
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                success = false,
                error = $"Storage quota exceeded ({usage.UsedBytes / (1024.0 * 1024 * 1024):F1} GB used of {usage.IncludedStorageGb} GB included). New DICOM uploads are blocked — free up space or upgrade the plan. Existing studies remain viewable.",
                errorCode = "STORAGE_QUOTA_EXCEEDED",
            });
        }

        // Phase 1 of the RIS/PACS split: every DICOM-bearing asset belongs to
        // an ImagingStudy aggregate. Re-uploads keep the asset's existing
        // study; otherwise one is created seeded from the appointment —
        // extraction later refines it with the real DICOM tags
        // (StudyInstanceUID, modality, description) and flips its Status.
        // `directlyViewable` = single .dcm files that skip extraction.
        private void EnsureImagingStudy(StudyAsset asset, Appointment appointment, string source, bool directlyViewable)
        {
            if (asset.ImagingStudyId != null) return;
            var study = new ImagingStudy
            {
                Id = Guid.NewGuid(),
                HospitalId = appointment.HospitalId,
                PatientId = appointment.PatientId,
                PatientName = appointment.PatientName,
                Modality = appointment.Modality,
                StudyDate = appointment.DateTime,
                Status = directlyViewable ? ImagingStudyStatus.Ready : ImagingStudyStatus.Received,
                ReadyAt = directlyViewable ? DateTime.UtcNow : null,
                Source = source,
                AppointmentId = appointment.AppointmentId,
                AppointmentServiceId = asset.AppointmentServiceId,
            };
            _context.ImagingStudies.Add(study);
            asset.ImagingStudyId = study.Id;
        }

        // Returns null when the upload is allowed; otherwise the 403 to return.
        // DICOM-typed files require the PACS module on the active center.
        private async Task<IActionResult?> RequireDicomCapabilityAsync(string fileName)
        {
            var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
            if (!DicomExtensions.Contains(ext)) return null;
            if (await _modules.HasModuleAsync(_userContext.HospitalId, ModuleConstants.Pacs))
                return null;
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                success = false,
                error = "DICOM upload requires the PACS module, which is not part of this center's subscription. Only document attachments (PDF/JPG/PNG) are available on a RIS-only plan.",
                errorCode = "MODULE_NOT_ENABLED",
                module = ModuleConstants.Pacs,
            });
        }

        // Tenant guard for the upload paths. Those endpoints deliberately load
        // the appointment with IgnoreQueryFilters() — in a multi-hospital group
        // the active center (UserContext.HospitalId) can legitimately differ
        // from the appointment's center — which also disables the global
        // HospitalId isolation filter. Without this check any authenticated
        // user could attach assets to ANY hospital's appointment by guessing an
        // id. Allow the caller's own active center, or any group center carried
        // in the token's "hubs" claim. Returns null when allowed, else the 403.
        private IActionResult? EnsureHospitalAccess(Guid hospitalId)
        {
            if (hospitalId == _userContext.HospitalId) return null;
            if (_userContext.AuthorizedHospitalIds.Contains(hospitalId)) return null;
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                success = false,
                error = "This appointment belongs to a center you are not authorized to access.",
                errorCode = "HOSPITAL_FORBIDDEN",
            });
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
        // Extract the blob path (everything after "/{container}/") from a full
        // blob URL, so we can existence-check a staged instance from its
        // PublicReadUrl. Returns null if the URL can't be parsed.
        private static string? BlobPathFromUrl(string url, string container)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                var abs = new Uri(url).AbsolutePath.TrimStart('/'); // "{container}/{path}"
                var prefix = container + "/";
                return abs.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? abs.Substring(prefix.Length)
                    : abs;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Resolves the Front Door base URL from configuration, tolerant of how
        /// the App Setting is keyed. Azure App Service injects settings as env
        /// vars: on Windows a "AzureBlobStorage:CdnBaseUrl" colon key binds, but
        /// on LINUX/containers nested keys MUST use "__" (double underscore) —
        /// a literal ":" setting simply doesn't bind there and the lookup returns
        /// null, so every slice silently bypasses Front Door. We read all the
        /// plausible forms so the setting works regardless of platform/keying.
        /// </summary>
        public static string? ResolveCdnBaseUrl(IConfiguration config)
        {
            string? Pick(params string?[] vals) =>
                vals.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            return Pick(
                config["AzureBlobStorage:CdnBaseUrl"],
                config["AzureBlobStorage__CdnBaseUrl"],
                config["CdnBaseUrl"],
                Environment.GetEnvironmentVariable("AzureBlobStorage__CdnBaseUrl"),
                Environment.GetEnvironmentVariable("AzureBlobStorage:CdnBaseUrl"),
                Environment.GetEnvironmentVariable("CdnBaseUrl"));
        }

        private string? ToCdn(string? blobUrl)
        {
            if (string.IsNullOrEmpty(blobUrl)) return blobUrl;
            var cdnBase = ResolveCdnBaseUrl(_configuration);
            if (string.IsNullOrWhiteSpace(cdnBase)) return blobUrl;
            // Tolerate scheme-less configuration ("myfd.azurefd.net"). Without
            // a scheme the emitted URL is RELATIVE — the browser resolves it
            // against the SPA origin and gets index.html instead of DICOM
            // bytes (the viewer then fails with "DICM prefix not found").
            if (!cdnBase.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !cdnBase.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                cdnBase = "https://" + cdnBase;
            }
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

        // Frame-blob derivation lives in DicomExtractionService (its owner). The
        // delete loop above calls it via this alias so both stay in lock-step.
        private static string? FrameUrlFromSlice(string? sliceBlobUrl)
            => _1Rad.Infrastructure.Services.DicomExtractionService.FrameUrlFromSlice(sliceBlobUrl);

        /// <summary>
        /// Pulls the raw `frameUrl` (blob URL) out of a slice's metadata JSON for
        /// the byte-range progressive path. Returns null if absent (legacy slice
        /// with no streamable frame) so the viewer falls back to the .dcm.
        /// </summary>
        private static string? ExtractFrameUrl(string? metadataJson) => ExtractMetaUrl(metadataJson, "frameUrl");
        private static string? ExtractPreviewUrl(string? metadataJson) => ExtractMetaUrl(metadataJson, "previewUrl");

        private static string? ExtractMetaUrl(string? metadataJson, string key)
        {
            if (string.IsNullOrWhiteSpace(metadataJson)) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
                return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                    ? v.GetString()
                    : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// SAS lifetime scaled to file size. A near-1 GB upload on a slow
        /// (low-bandwidth) link can outlive a fixed 30-min SAS and fail
        /// mid-transfer. Base 30 min + headroom (~4 MB/min pessimistic), capped
        /// at 6h. The token is Write-only on a single blob path, so the longer
        /// lifetime's exposure is limited to that one blob.
        /// </summary>
        private static TimeSpan SasValidityFor(long fileSizeBytes)
        {
            var mb = Math.Max(0, fileSizeBytes) / 1_048_576.0;
            var minutes = Math.Clamp(30 + mb / 4.0, 30, 360);
            return TimeSpan.FromMinutes(minutes);
        }

        /// <summary>
        /// Returns the slice metadata JSON with the raw `frameUrl` removed (the
        /// manifest exposes a CDN-rewritten copy separately). Keeps the pixel
        /// module the wadors metadata provider needs.
        /// </summary>
        private static string? StripFrameUrl(string? metadataJson)
        {
            if (string.IsNullOrWhiteSpace(metadataJson)) return metadataJson;
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(metadataJson);
                if (node is System.Text.Json.Nodes.JsonObject obj)
                {
                    var changed = false;
                    // Both raw blob URLs are re-exposed CDN-rewritten on the slice
                    // object; strip them from the metadata copy (a raw blob URL in
                    // the payload is a Front-Door-bypass footgun).
                    if (obj.Remove("frameUrl")) changed = true;
                    if (obj.Remove("previewUrl")) changed = true;
                    if (changed) return obj.ToJsonString();
                }
                return metadataJson;
            }
            catch
            {
                return metadataJson;
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
        [RequiresModule(ModuleConstants.Pacs)]
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

            // Lazy fallback: legacy uploads from before their type became
            // extraction-eligible (ZIPs without a status; single DCMs that
            // were passthrough) — ENQUEUE for the background worker instead of
            // extracting inline. Blocking this request meant the first viewer
            // open of a legacy study could hang for minutes; the frontend
            // already polls and renders a "processing" state.
            var lazyAppointment = assets.Where(NeedsLazyExtraction).ToList();
            if (lazyAppointment.Count > 0)
            {
                foreach (var a in lazyAppointment) a.ExtractionStatus = "Queued";
                await _context.SaveChangesAsync(cancellationToken);
                foreach (var a in lazyAppointment) _extractionQueue.Enqueue(a.Id);
            }

            // Reload with slices included for the manifest DTOs.
            assets = await _context.StudyAssets
                .Where(a => a.AppointmentId == appointment.AppointmentId)
                .Include(a => a.Slices)
                .OrderByDescending(a => a.UploadedAt)
                .ToListAsync(cancellationToken);

            var assetDtos = BuildManifestAssetDtos(assets);

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

        /// <summary>
        /// Manifest keyed by ImagingStudy (Phase 1 of the RIS/PACS split) —
        /// the appointment-free entry point PACS-only surfaces will use. Same
        /// payload shape as the by-appointment manifest, so the viewer's
        /// loader is shared; appointment fields are null for unlinked studies.
        /// </summary>
        [HttpGet("by-study/{imagingStudyId:guid}/manifest")]
        [RequiresModule(ModuleConstants.Pacs)]
        public async Task<IActionResult> GetManifestByStudy(Guid imagingStudyId, CancellationToken cancellationToken)
        {
            var study = await _context.ImagingStudies
                .FirstOrDefaultAsync(st => st.Id == imagingStudyId, cancellationToken);
            if (study == null)
                return NotFound(new { success = false, error = "Imaging study not found." });

            var assets = await _context.StudyAssets
                .Where(a => a.ImagingStudyId == study.Id)
                .Include(a => a.Slices)
                .OrderByDescending(a => a.UploadedAt)
                .ToListAsync(cancellationToken);

            // Same lazy-extraction fallback as the by-appointment route, but
            // NON-BLOCKING: enqueue for the worker; the frontend polls and
            // shows "processing" until slices are ready.
            var lazyStudy = assets.Where(NeedsLazyExtraction).ToList();
            if (lazyStudy.Count > 0)
            {
                foreach (var a in lazyStudy) a.ExtractionStatus = "Queued";
                await _context.SaveChangesAsync(cancellationToken);
                foreach (var a in lazyStudy) _extractionQueue.Enqueue(a.Id);
            }

            return Ok(new
            {
                success = true,
                data = new
                {
                    imagingStudyId = study.Id,
                    studyInstanceUID = study.StudyInstanceUID,
                    status = study.Status,
                    appointmentId = study.AppointmentId,
                    patientName = study.PatientName,
                    modality = study.Modality,
                    studyDate = study.StudyDate,
                    studyDescription = study.StudyDescription,
                    assets = BuildManifestAssetDtos(assets),
                },
            });
        }

        /// <summary>
        /// Lightweight extraction-status poll — the viewer hits this every few
        /// seconds while a study is processing. The full manifest (all slice
        /// rows + metadata JSON) is only fetched once something is actually
        /// ready; previously the poll re-ran the heavy manifest query each tick.
        /// </summary>
        [HttpGet("by-study/{imagingStudyId:guid}/extraction-status")]
        [RequiresModule(ModuleConstants.Pacs)]
        public async Task<IActionResult> GetExtractionStatusByStudy(Guid imagingStudyId, CancellationToken cancellationToken)
        {
            var study = await _context.ImagingStudies
                .AsNoTracking()
                .Where(st => st.Id == imagingStudyId)
                .Select(st => new { st.Id, st.Status })
                .FirstOrDefaultAsync(cancellationToken);
            if (study == null)
                return NotFound(new { success = false, error = "Imaging study not found." });

            // Live progress (phase + slices done/total/percent) is read straight
            // from the row, so it's correct no matter which instance is doing the
            // extraction (durable, multi-instance).
            var assets = await _context.StudyAssets
                .AsNoTracking()
                .Where(a => a.ImagingStudyId == imagingStudyId)
                .Select(a => new
                {
                    assetId = a.Id, fileName = a.FileName, fileType = a.FileType,
                    extractionStatus = a.ExtractionStatus,
                    phase     = a.ExtractionPhase,
                    processed = a.ExtractionProcessedSlices,
                    total     = a.ExtractionTotalSlices,
                    percent   = a.ExtractionTotalSlices > 0
                        ? (int)Math.Round(100.0 * a.ExtractionProcessedSlices / a.ExtractionTotalSlices)
                        : 0,
                })
                .ToListAsync(cancellationToken);

            return Ok(new { success = true, data = new { status = study.Status, assets } });
        }

        // ── Secure share links ──────────────────────────────────────────────
        /// <summary>
        /// Mint a 24-hour secret link to share a study with an external doctor.
        /// The token is HMAC-signed (stateless) and carries the study id + expiry.
        /// Tenant-scoped: the study must belong to the caller's centre.
        /// </summary>
        [HttpPost("studies/{studyId:guid}/share")]
        [RequiresModule(ModuleConstants.Pacs)]
        public async Task<IActionResult> CreateShareLink(Guid studyId)
        {
            // Global query filter ⇒ only the caller's-tenant study resolves.
            var study = await _context.ImagingStudies.FirstOrDefaultAsync(s => s.Id == studyId);
            if (study == null)
                return NotFound(new { success = false, error = "Imaging study not found." });

            var ttl = TimeSpan.FromHours(24);
            var token = _shareTokens.Issue(study.Id, ttl);
            var expiresAt = DateTimeOffset.UtcNow.Add(ttl);
            return Ok(new { success = true, data = new { token, expiresAt, ttlHours = 24 } });
        }

        /// <summary>
        /// Public manifest for a shared study — no auth, gated entirely by the
        /// signed token. Returns 410 (SHARE_EXPIRED) once the 24h window passes
        /// so the share page can show the "expired" + upgrade message, or 404 for
        /// a tampered/invalid token.
        /// </summary>
        [HttpGet("shared/{token}/manifest")]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public async Task<IActionResult> GetSharedManifest(string token, CancellationToken cancellationToken)
        {
            var (status, studyId, _) = _shareTokens.Validate(token);
            if (status == ShareTokenStatus.Invalid)
                return NotFound(new { success = false, code = "SHARE_INVALID", error = "This share link is not valid." });
            if (status == ShareTokenStatus.Expired)
                return StatusCode(StatusCodes.Status410Gone, new { success = false, code = "SHARE_EXPIRED", error = "This share link has expired." });

            // Valid token → resolve across tenants (no auth context here).
            var study = await _context.ImagingStudies
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(st => st.Id == studyId, cancellationToken);
            if (study == null)
                return NotFound(new { success = false, code = "SHARE_INVALID", error = "The shared study no longer exists." });

            var assets = await _context.StudyAssets
                .IgnoreQueryFilters()
                .Where(a => a.ImagingStudyId == study.Id)
                .Include(a => a.Slices)
                .OrderByDescending(a => a.UploadedAt)
                .ToListAsync(cancellationToken);

            return Ok(new
            {
                success = true,
                data = new
                {
                    imagingStudyId = study.Id,
                    studyInstanceUID = study.StudyInstanceUID,
                    status = study.Status,
                    patientName = study.PatientName,
                    modality = study.Modality,
                    studyDate = study.StudyDate,
                    studyDescription = study.StudyDescription,
                    assets = BuildManifestAssetDtos(assets),
                },
            });
        }

        /// <summary>
        /// Re-queue extraction for a failed (or stuck) study — the "Retry"
        /// action on the worklist. Resets the study + its extractable assets to
        /// Queued and re-enqueues them. Tenant-scoped via the global filter.
        /// </summary>
        [HttpPost("studies/{studyId:guid}/reextract")]
        [RequiresModule(ModuleConstants.Pacs)]
        public async Task<IActionResult> ReextractStudy(Guid studyId)
        {
            var study = await _context.ImagingStudies.FirstOrDefaultAsync(s => s.Id == studyId);
            if (study == null)
                return NotFound(new { success = false, error = "Imaging study not found." });

            var assets = await _context.StudyAssets
                .Where(a => a.ImagingStudyId == study.Id)
                .ToListAsync();
            var retryable = assets.Where(a => NeedsExtraction(a.FileType) || a.FileType == "instances").ToList();
            if (retryable.Count == 0)
                return BadRequest(new { success = false, error = "This study has no extractable assets to retry." });

            foreach (var a in retryable)
            {
                // Manual retry starts FRESH: clear the durable retry/backoff +
                // lease state so the leased queue claims it immediately (not gated
                // by a stale backoff) and the attempt counter restarts.
                a.ExtractionStatus       = "Queued";
                a.ExtractionError        = null;
                a.ExtractionAttempts     = 0;
                a.ExtractionNextAttemptAt = null;
                a.ExtractionLeaseOwner   = null;
                a.ExtractionLeaseUntil   = null;
                a.ExtractionPhase        = null;
                a.ExtractionProcessedSlices = 0;
            }
            study.Status = ImagingStudyStatus.Processing;
            await _context.SaveChangesAsync(default);
            foreach (var a in retryable) _extractionQueue.Enqueue(a.Id);

            return Ok(new { success = true, data = new { imagingStudyId = study.Id, requeued = retryable.Count } });
        }

        /// <summary>Same lightweight poll, keyed by appointment.</summary>
        [HttpGet("{appointmentId}/extraction-status")]
        [RequiresModule(ModuleConstants.Pacs)]
        public async Task<IActionResult> GetExtractionStatus(string appointmentId, CancellationToken cancellationToken)
        {
            Guid.TryParse(appointmentId, out var guidId);
            var appointment = await _context.Appointments
                .AsNoTracking()
                .Where(a => (guidId != Guid.Empty && a.AppointmentId == guidId) || a.DisplayId == appointmentId)
                .Select(a => new { a.AppointmentId })
                .FirstOrDefaultAsync(cancellationToken);
            if (appointment == null)
                return NotFound(new { success = false, error = "Appointment not found." });

            var assets = await _context.StudyAssets
                .AsNoTracking()
                .Where(a => a.AppointmentId == appointment.AppointmentId)
                .Select(a => new
                {
                    assetId = a.Id, fileName = a.FileName, fileType = a.FileType,
                    extractionStatus = a.ExtractionStatus,
                    phase     = a.ExtractionPhase,
                    processed = a.ExtractionProcessedSlices,
                    total     = a.ExtractionTotalSlices,
                    percent   = a.ExtractionTotalSlices > 0
                        ? (int)Math.Round(100.0 * a.ExtractionProcessedSlices / a.ExtractionTotalSlices)
                        : 0,
                })
                .ToListAsync(cancellationToken);

            return Ok(new { success = true, data = new { status = (string?)null, assets } });
        }

        // Per-asset manifest DTOs — shared by the by-appointment and by-study
        // manifest endpoints so the viewer consumes one shape everywhere.
        /// <summary>
        /// File types the extraction worker normalises into per-slice HTJ2K
        /// blobs. Single .dcm files are included so preamble-less DICOMs
        /// (common from modality exports) come out as valid P10 the browser
        /// parser accepts — serving the raw upload broke the viewer with
        /// "DICM prefix not found".
        /// </summary>
        private static bool NeedsExtraction(string? fileType)
        {
            var t = (fileType ?? "").Trim().ToLowerInvariant();
            return t == "zip" || t == "dcm" || t == "dicom";
        }

        /// <summary>
        /// True for assets that should lazily extract on first manifest hit:
        /// legacy rows uploaded before their type was extraction-eligible.
        /// ZIPs: no status at all. Single DCMs: no status OR the old
        /// "NotApplicable" marker (they were passthrough before).
        /// </summary>
        private static bool NeedsLazyExtraction(StudyAsset a)
        {
            var t = (a.FileType ?? "").Trim().ToLowerInvariant();
            if (t == "zip") return string.IsNullOrEmpty(a.ExtractionStatus);
            if (t == "dcm" || t == "dicom")
                return string.IsNullOrEmpty(a.ExtractionStatus) || a.ExtractionStatus == "NotApplicable";
            return false;
        }

        private List<object> BuildManifestAssetDtos(List<StudyAsset> assets)
        {
            return assets.Select(a =>
            {
                // ZIPs, per-instance ("instances") uploads and single DCMs all
                // extract into the slice index. (Previously only "zip" was
                // checked, which sent instances-assets down the passthrough
                // branch — handing the viewer the staging _manifest.json URL
                // instead of the series.)
                var t = (a.FileType ?? "").Trim().ToLowerInvariant();
                var extractable = t == "zip" || t == "instances" || t == "dcm" || t == "dicom";
                if (!extractable)
                {
                    // Pass-through for non-extractable attachments (single DCM, JPG, PNG).
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
                            // Thumbnail is written on one slice of the series (the
                            // first); read it from whichever slice carries it so a
                            // re-ordering or partial extraction can't blank the rail.
                            thumbnailUrl = ToCdn(g.Select(s => s.ThumbnailUrl).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? first.ThumbnailUrl),
                            slices = g.OrderBy(s => s.InstanceNumber ?? int.MaxValue)
                                      .ThenBy(s => s.SopInstanceUID)
                                      .Select(s => new
                                      {
                                          sopInstanceUID = s.SopInstanceUID,
                                          instanceNumber = s.InstanceNumber,
                                          url = ToCdn(s.BlobUrl),
                                          // Raw HTJ2K frame for byte-range progressive
                                          // loading — lifted out of the metadata JSON and
                                          // CDN-rewritten so it goes through Front Door
                                          // like every other slice URL. Null when the
                                          // slice has no streamable frame (legacy / non-
                                          // HTJ2K) → viewer uses the .dcm `url`.
                                          frameUrl = ToCdn(ExtractFrameUrl(s.MetadataJson)),
                                          // Tiny progressive-preview JPEG (blurry→sharp two-tier load),
                                          // CDN-rewritten. Null when previews weren't generated → viewer
                                          // just loads the full slice directly.
                                          previewUrl = ToCdn(ExtractPreviewUrl(s.MetadataJson)),
                                          // Strip the raw (non-CDN) frameUrl from the metadata copy — the
                                          // CDN sibling above is the one to use; a raw blob URL in the
                                          // payload is a Front-Door-bypass footgun.
                                          metadata = StripFrameUrl(s.MetadataJson),
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
            }).Cast<object>().ToList();
        }

        [HttpGet("{appointmentId}/viewer")]
        [RequiresModule(ModuleConstants.Pacs)]
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

                // RIS-only centers can attach documents but not DICOM.
                var moduleBlock = await RequireDicomCapabilityAsync(request.File.FileName);
                if (moduleBlock != null) return moduleBlock;

                // Quota applies to DICOM ingestion only (documents are tiny).
                var uploadExt = Path.GetExtension(request.File.FileName ?? string.Empty).ToLowerInvariant();
                if (DicomExtensions.Contains(uploadExt))
                {
                    var quotaBlock = await RequireStorageHeadroomAsync(_userContext.HospitalId);
                    if (quotaBlock != null) return quotaBlock;
                }

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

                var tenantBlock = EnsureHospitalAccess(appointment.HospitalId);
                if (tenantBlock != null) return tenantBlock;

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
                    existingAsset.StorageBytes = request.File.Length; // re-upload replaces the blob
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
                        StorageBytes = request.File.Length,
                        HospitalId = appointment.HospitalId // Inherit from appointment to prevent FK conflict
                    };
                    _context.StudyAssets.Add(asset);
                }
                
                // Auto-update status to IN_PROGRESS if first asset
                if (appointment.Status != "SCANNED" && appointment.Status != "REPORTED")
                {
                    appointment.Status = "IN_PROGRESS";
                }

                // Mark for DICOM extraction (Option C). ZIPs and single DCMs
                // are normalised by the worker; other types are NotApplicable.
                if (NeedsExtraction(asset.FileType))
                {
                    asset.ExtractionStatus = "Queued";
                }

                if (IsImagingFileType(asset.FileType))
                    EnsureImagingStudy(asset, appointment, "api-upload",
                        directlyViewable: !NeedsExtraction(asset.FileType));

                await _context.SaveChangesAsync(default);
                _storage.Invalidate(appointment.HospitalId);

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

                // RIS-only centers can SAS-upload documents but not DICOM/ZIP.
                // Gating the token is sufficient for the whole SAS flow: without
                // a token nothing can be staged, so /upload-complete can't be
                // reached for a blocked file type.
                var moduleBlock = await RequireDicomCapabilityAsync(request.FileName);
                if (moduleBlock != null) return moduleBlock;

                var tokenExt = Path.GetExtension(request.FileName).ToLowerInvariant();
                if (DicomExtensions.Contains(tokenExt))
                {
                    var quotaBlock = await RequireStorageHeadroomAsync(_userContext.HospitalId);
                    if (quotaBlock != null) return quotaBlock;
                }

                // Hard size cap — adjust if you ever need bigger studies.
                const long MaxBytes = 1_073_741_824L; // 1 GB
                if (request.FileSize > MaxBytes)
                    return BadRequest(new { success = false, error = $"File too large. Maximum allowed is {MaxBytes / (1024 * 1024)} MB." });

                var appointment = await _context.Appointments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId);

                if (appointment == null)
                    return NotFound(new { success = false, error = "Appointment not found." });

                var tenantBlock = EnsureHospitalAccess(appointment.HospitalId);
                if (tenantBlock != null) return tenantBlock;

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
                        SasValidityFor(request.FileSize),
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
                    || string.IsNullOrWhiteSpace(request.FileName))
                {
                    // PublicReadUrl is no longer required — it's a client-echoed
                    // value we deliberately don't trust (see below); the read URL
                    // is derived server-side from the verified container + path.
                    return BadRequest(new { success = false, error = "AssetId, AppointmentId, BlobPath, ContainerName and FileName are all required." });
                }

                var appointment = await _context.Appointments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId);
                if (appointment == null)
                    return NotFound(new { success = false, error = "Appointment not found." });

                var tenantBlock = EnsureHospitalAccess(appointment.HospitalId);
                if (tenantBlock != null) return tenantBlock;

                // Bind the blob to this appointment's tenant. The path we minted
                // in /upload-token is "{hospitalId:N}/{appointmentId:N}/...";
                // requiring that prefix stops a caller from confirming against an
                // existing blob that belongs to another center (which would then
                // be stored as this appointment's asset and served to its users).
                var expectedPrefix = $"{appointment.HospitalId:N}/{appointment.AppointmentId:N}/";
                if (!request.BlobPath.Replace('\\', '/').TrimStart('/')
                        .StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { success = false, error = "BlobPath does not belong to this appointment." });
                }

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

                // Canonical read URL derived from the verified container + path on
                // OUR account — never the client-echoed PublicReadUrl, which the
                // caller could point at someone else's blob.
                var readUrl = _blobService.GetBlobReadUrl(request.BlobPath, request.ContainerName);

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
                        BlobUrl = readUrl,
                        FileName = fileName,
                        FileType = string.IsNullOrEmpty(extension) ? "bin" : extension,
                        UploadedAt = DateTime.UtcNow,
                        HospitalId = appointment.HospitalId,
                    };
                    _context.StudyAssets.Add(asset);
                }
                else
                {
                    asset.BlobUrl = readUrl;
                    asset.UploadedAt = DateTime.UtcNow;
                    if (request.AppointmentServiceId.HasValue)
                        asset.AppointmentServiceId = request.AppointmentServiceId;
                }

                // Meter the verified blob (Phase 3). Extraction recomputes the
                // durable total (blob + slices) for ZIPs later.
                asset.StorageBytes = await _blobService.GetBlobSizeAsync(request.BlobPath, request.ContainerName);
                _storage.Invalidate(appointment.HospitalId);

                if (appointment.Status != "SCANNED" && appointment.Status != "REPORTED")
                {
                    appointment.Status = "IN_PROGRESS";
                }

                // Mark for DICOM extraction (Option C). ZIPs and single DCMs
                // are normalised by the worker; other types are NotApplicable.
                if (NeedsExtraction(asset.FileType))
                {
                    asset.ExtractionStatus = "Queued";
                }

                if (IsImagingFileType(asset.FileType))
                    EnsureImagingStudy(asset, appointment, "sas-upload",
                        directlyViewable: !NeedsExtraction(asset.FileType));

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

        // ── Per-instance ingest (SAS-per-file) ──────────────────────────────
        // The bridge uploads each DICOM instance straight to a staging blob via
        // SAS (no whole-study ZIP), then calls /register. Removes the server
        // unzip step; the existing extraction worker transcodes the staged
        // instances to HTJ2K and builds the slice index exactly as for a ZIP.

        [HttpPost("instance-upload/request-sas")]
        [RequiresModule(ModuleConstants.Pacs)]
        public async Task<IActionResult> RequestInstanceUploadSas([FromBody] InstanceUploadSasRequest request)
        {
            try
            {
                if (request == null || request.AppointmentId == Guid.Empty)
                    return BadRequest(new { success = false, error = "AppointmentId is required." });
                if (request.Count <= 0 || request.Count > 5000)
                    return BadRequest(new { success = false, error = "Count must be between 1 and 5000." });

                var appointment = await _context.Appointments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId);
                if (appointment == null)
                    return NotFound(new { success = false, error = "Appointment not found." });

                var tenantBlock = EnsureHospitalAccess(appointment.HospitalId);
                if (tenantBlock != null) return tenantBlock;

                var quotaBlock = await RequireStorageHeadroomAsync(appointment.HospitalId);
                if (quotaBlock != null) return quotaBlock;

                // Mint the asset id now so staging blobs live under it; /register
                // and the worker find + reference them deterministically.
                var assetId = Guid.NewGuid();
                var targets = new List<object>(request.Count);
                for (int i = 0; i < request.Count; i++)
                {
                    var blobPath = $"{appointment.HospitalId:N}/{appointment.AppointmentId:N}/staging/{assetId:N}/{i:D5}.dcm";
                    var t = await _blobService.GenerateSasUploadUrlAsync(
                        blobPath, "dicom-files", TimeSpan.FromHours(2), "application/dicom");
                    targets.Add(new { index = i, sasUrl = t.SasUrl, publicReadUrl = t.PublicReadUrl, blobPath = t.BlobPath });
                }

                return Ok(new { success = true, data = new { assetId, containerName = "dicom-files", targets } });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"INSTANCE_SAS_FAILURE: {ex.Message}" });
            }
        }

        [HttpPost("instance-upload/register")]
        [RequiresModule(ModuleConstants.Pacs)]
        public async Task<IActionResult> RegisterInstanceUpload([FromBody] InstanceUploadRegisterRequest request)
        {
            try
            {
                if (request == null || request.AssetId == Guid.Empty || request.AppointmentId == Guid.Empty)
                    return BadRequest(new { success = false, error = "AssetId and AppointmentId are required." });
                if (request.InstanceUrls == null || request.InstanceUrls.Count == 0)
                    return BadRequest(new { success = false, error = "At least one staged instance URL is required." });

                var appointment = await _context.Appointments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId);
                if (appointment == null)
                    return NotFound(new { success = false, error = "Appointment not found." });

                var tenantBlock = EnsureHospitalAccess(appointment.HospitalId);
                if (tenantBlock != null) return tenantBlock;

                // Bind every staged instance to this appointment + asset. The SAS
                // paths we minted in /instance-upload/request-sas all live under
                // "{hospitalId:N}/{appointmentId:N}/staging/{assetId:N}/". Without
                // this check a caller could register blob URLs pointing at another
                // center's data and have the worker fold them into this study.
                var stagingPrefix = $"{appointment.HospitalId:N}/{appointment.AppointmentId:N}/staging/{request.AssetId:N}/";
                foreach (var url in request.InstanceUrls)
                {
                    var p = BlobPathFromUrl(url, "dicom-files");
                    if (p == null || !p.Replace('\\', '/').TrimStart('/')
                            .StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return BadRequest(new { success = false, error = "One or more instance URLs do not belong to this appointment's staging area." });
                    }
                }

                // Parity with /upload-complete — verify the staged blobs actually
                // landed before creating an asset that references them. Sample
                // the first + last (full N HEAD requests would be slow for a
                // 200-slice study); a sample catches the gross failure modes:
                // wrong container, CORS, or every PUT having silently failed.
                var sampleUrls = new List<string> { request.InstanceUrls[0] };
                if (request.InstanceUrls.Count > 1)
                    sampleUrls.Add(request.InstanceUrls[request.InstanceUrls.Count - 1]);
                foreach (var url in sampleUrls)
                {
                    var path = BlobPathFromUrl(url, "dicom-files");
                    if (path == null || !await _blobService.BlobExistsAsync(path, "dicom-files"))
                    {
                        return BadRequest(new
                        {
                            success = false,
                            error = "Staged instance blob not found in Azure — the PUT likely failed (CORS not configured on the storage account, or the SAS expired) before register. Re-upload the instances and retry.",
                        });
                    }
                }

                // Write a tiny manifest listing the staged instance blobs. The
                // extraction worker reads it (asset.BlobUrl points here), then
                // transcodes each to HTJ2K + builds the slice index. The JSON
                // property name ("Instances") matches the worker's reader.
                var manifestJson = System.Text.Json.JsonSerializer.Serialize(new { Instances = request.InstanceUrls });
                var manifestPath = $"{appointment.HospitalId:N}/{appointment.AppointmentId:N}/staging/{request.AssetId:N}/_manifest.json";
                string manifestUrl;
                using (var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifestJson)))
                {
                    manifestUrl = await _blobService.UploadFileAtPathAsync(ms, manifestPath, "application/json", "dicom-files");
                }

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
                        BlobUrl = manifestUrl,
                        FileName = $"study_{request.InstanceUrls.Count}_instances",
                        FileType = "instances",
                        UploadedAt = DateTime.UtcNow,
                        HospitalId = appointment.HospitalId,
                        ExtractionStatus = "Queued",
                    };
                    _context.StudyAssets.Add(asset);
                }
                else
                {
                    asset.BlobUrl = manifestUrl;
                    asset.FileType = "instances";
                    asset.UploadedAt = DateTime.UtcNow;
                    asset.ExtractionStatus = "Queued";
                    if (request.AppointmentServiceId.HasValue)
                        asset.AppointmentServiceId = request.AppointmentServiceId;
                }

                EnsureImagingStudy(asset, appointment, "bridge", directlyViewable: false);

                if (appointment.Status != "SCANNED" && appointment.Status != "REPORTED")
                    appointment.Status = "IN_PROGRESS";

                await _context.SaveChangesAsync(default);
                _extractionQueue.Enqueue(asset.Id);

                return Ok(new { success = true, data = asset });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"INSTANCE_REGISTER_FAILURE: {ex.Message}" });
            }
        }

        // ── Cloud PACS-only: appointment-free study ingestion ───────────────
        // These mirror the appointment-based endpoints above, but a study —
        // not an appointment — is the owner. Assets are stamped with
        // ImagingStudyId and AppointmentId = null; blobs are foldered by
        // {hospital}/{study}/... which the extraction worker already handles
        // (it scopes by AppointmentId ?? ImagingStudyId ?? assetId). All are
        // PACS-only surfaces, so they carry [RequiresModule(PACS)].

        // Loads a study for a write and tenant-guards it. IgnoreQueryFilters so
        // a group user's active center can differ from the study's center (the
        // guard, not the filter, enforces access — same pattern as uploads).
        private async Task<(ImagingStudy? study, IActionResult? error)> LoadStudyForWriteAsync(Guid studyId)
        {
            var study = await _context.ImagingStudies
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.Id == studyId);
            if (study == null)
                return (null, NotFound(new { success = false, error = "Imaging study not found." }));
            var block = EnsureHospitalAccess(study.HospitalId);
            if (block != null) return (null, block);
            return (study, null);
        }

        [HttpPost("studies/register")]
        [RequiresModule(ModuleConstants.Pacs)]
        public async Task<IActionResult> RegisterStudy([FromBody] StudyRegisterRequest request)
        {
            try
            {
                if (request == null) return BadRequest(new { success = false, error = "Request body is required." });
                var hospitalId = _userContext.HospitalId;

                // Upsert by (HospitalId, StudyInstanceUID) when the UID is known
                // (the web Upload Center supplies it). Otherwise always a new row.
                ImagingStudy? study = null;
                var uid = string.IsNullOrWhiteSpace(request.StudyInstanceUID) ? null : request.StudyInstanceUID.Trim();
                if (uid != null)
                {
                    study = await _context.ImagingStudies
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(s => s.HospitalId == hospitalId && s.StudyInstanceUID == uid);
                }

                if (study == null)
                {
                    study = new ImagingStudy
                    {
                        Id = Guid.NewGuid(),
                        HospitalId = hospitalId,
                        StudyInstanceUID = uid,
                        PatientName = request.PatientName,
                        DicomPatientId = request.DicomPatientId,
                        AccessionNumber = request.AccessionNumber,
                        Modality = request.Modality,
                        StudyDate = request.StudyDate,
                        StudyDescription = request.StudyDescription,
                        Source = string.IsNullOrWhiteSpace(request.Source) ? "web-upload" : request.Source,
                        Status = ImagingStudyStatus.Received,
                        MatchStatus = ImagingStudyMatchStatus.Unmatched,
                        CreatedAt = DateTime.UtcNow,
                    };
                    _context.ImagingStudies.Add(study);
                    await _context.SaveChangesAsync(default);
                    // Server-side matching is applied here (and again after
                    // extraction refines the real DICOM tags) — see
                    // IStudyMatchingService wiring.
                    await _matching.TryMatchAsync(study, default);
                    await _context.SaveChangesAsync(default);
                }
                else
                {
                    // Fill any demographics the caller now supplies (don't clobber).
                    study.PatientName ??= request.PatientName;
                    study.DicomPatientId ??= request.DicomPatientId;
                    study.AccessionNumber ??= request.AccessionNumber;
                    study.Modality ??= request.Modality;
                    study.StudyDate ??= request.StudyDate;
                    study.StudyDescription ??= request.StudyDescription;
                    await _context.SaveChangesAsync(default);
                }

                return Ok(new
                {
                    success = true,
                    data = new { imagingStudyId = study.Id, status = study.Status, matchStatus = study.MatchStatus },
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"STUDY_REGISTER_FAILURE: {ex.Message}" });
            }
        }

        [HttpPost("studies/{studyId:guid}/upload-token")]
        [RequiresModule(ModuleConstants.Pacs)]
        public async Task<IActionResult> RequestStudyUploadToken(Guid studyId, [FromBody] StudyUploadTokenRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.FileName))
                    return BadRequest(new { success = false, error = "FileName is required." });

                var (study, error) = await LoadStudyForWriteAsync(studyId);
                if (error != null) return error;

                var quotaBlock = await RequireStorageHeadroomAsync(study!.HospitalId);
                if (quotaBlock != null) return quotaBlock;

                const long MaxBytes = 1_073_741_824L; // 1 GB
                if (request.FileSize > MaxBytes)
                    return BadRequest(new { success = false, error = $"File too large. Maximum allowed is {MaxBytes / (1024 * 1024)} MB." });

                var fileName = Path.GetFileName(request.FileName);
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                var assetId = Guid.NewGuid();
                var blobPath = $"{study.HospitalId:N}/{study.Id:N}/{stamp}_{assetId:N}_{fileName}";

                SasUploadTarget target;
                try
                {
                    target = await _blobService.GenerateSasUploadUrlAsync(
                        blobPath, "dicom-files", SasValidityFor(request.FileSize), request.ContentType);
                }
                catch (Exception ex)
                {
                    return StatusCode(500, new { success = false, error = $"SAS_GENERATION_FAILURE: {ex.Message}" });
                }

                return Ok(new
                {
                    success = true,
                    data = new
                    {
                        assetId,
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
                return StatusCode(500, new { success = false, error = $"STUDY_UPLOAD_TOKEN_FAILURE: {ex.Message}" });
            }
        }

        [HttpPost("studies/{studyId:guid}/upload-complete")]
        [RequiresModule(ModuleConstants.Pacs)]
        public async Task<IActionResult> CompleteStudyUpload(Guid studyId, [FromBody] StudyUploadCompleteRequest request)
        {
            try
            {
                if (request == null
                    || request.AssetId == Guid.Empty
                    || string.IsNullOrWhiteSpace(request.BlobPath)
                    || string.IsNullOrWhiteSpace(request.ContainerName)
                    || string.IsNullOrWhiteSpace(request.FileName))
                {
                    return BadRequest(new { success = false, error = "AssetId, BlobPath, ContainerName and FileName are all required." });
                }

                var (study, error) = await LoadStudyForWriteAsync(studyId);
                if (error != null) return error;

                // Bind the blob to this study's tenant + id (see upload-complete).
                var expectedPrefix = $"{study!.HospitalId:N}/{study.Id:N}/";
                if (!request.BlobPath.Replace('\\', '/').TrimStart('/')
                        .StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { success = false, error = "BlobPath does not belong to this study." });
                }

                if (!await _blobService.BlobExistsAsync(request.BlobPath, request.ContainerName))
                {
                    return BadRequest(new { success = false, error = "Blob not found in Azure. The PUT may have failed or the SAS expired before upload finished." });
                }

                var fileName = Path.GetFileName(request.FileName);
                var extension = Path.GetExtension(fileName).ToLower().TrimStart('.');
                var readUrl = _blobService.GetBlobReadUrl(request.BlobPath, request.ContainerName);

                var asset = await _context.StudyAssets
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.Id == request.AssetId);
                if (asset == null)
                {
                    asset = new StudyAsset
                    {
                        Id = request.AssetId,
                        ImagingStudyId = study.Id,
                        AppointmentId = null,
                        BlobUrl = readUrl,
                        FileName = fileName,
                        FileType = string.IsNullOrEmpty(extension) ? "bin" : extension,
                        UploadedAt = DateTime.UtcNow,
                        HospitalId = study.HospitalId,
                    };
                    _context.StudyAssets.Add(asset);
                }
                else
                {
                    asset.BlobUrl = readUrl;
                    asset.UploadedAt = DateTime.UtcNow;
                    asset.ImagingStudyId = study.Id;
                }

                asset.StorageBytes = await _blobService.GetBlobSizeAsync(request.BlobPath, request.ContainerName);
                _storage.Invalidate(study.HospitalId);

                if (NeedsExtraction(asset.FileType))
                    asset.ExtractionStatus = "Queued";

                // Extractable types (ZIP + single DCM) flip to Processing→Ready
                // in the extraction worker — single DCMs are normalised there
                // too (preamble-less files come out as valid HTJ2K P10). Only
                // non-DICOM imaging attachments are Ready immediately.
                if (IsImagingFileType(asset.FileType)
                    && !NeedsExtraction(asset.FileType)
                    && study.Status == ImagingStudyStatus.Received)
                {
                    study.Status = ImagingStudyStatus.Ready;
                    study.ReadyAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync(default);

                if (asset.ExtractionStatus == "Queued")
                    _extractionQueue.Enqueue(asset.Id);

                return Ok(new { success = true, data = asset });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"STUDY_UPLOAD_COMPLETE_FAILURE: {ex.Message}" });
            }
        }

        [HttpPost("studies/{studyId:guid}/instance-upload/request-sas")]
        [RequiresModule(ModuleConstants.Pacs)]
        public async Task<IActionResult> RequestStudyInstanceSas(Guid studyId, [FromBody] StudyInstanceSasRequest request)
        {
            try
            {
                if (request == null || request.Count <= 0 || request.Count > 5000)
                    return BadRequest(new { success = false, error = "Count must be between 1 and 5000." });

                var (study, error) = await LoadStudyForWriteAsync(studyId);
                if (error != null) return error;

                var quotaBlock = await RequireStorageHeadroomAsync(study!.HospitalId);
                if (quotaBlock != null) return quotaBlock;

                var assetId = Guid.NewGuid();
                var targets = new List<object>(request.Count);
                for (int i = 0; i < request.Count; i++)
                {
                    var blobPath = $"{study.HospitalId:N}/{study.Id:N}/staging/{assetId:N}/{i:D5}.dcm";
                    var t = await _blobService.GenerateSasUploadUrlAsync(
                        blobPath, "dicom-files", TimeSpan.FromHours(2), "application/dicom");
                    targets.Add(new { index = i, sasUrl = t.SasUrl, publicReadUrl = t.PublicReadUrl, blobPath = t.BlobPath });
                }

                return Ok(new { success = true, data = new { assetId, containerName = "dicom-files", targets } });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"STUDY_INSTANCE_SAS_FAILURE: {ex.Message}" });
            }
        }

        [HttpPost("studies/{studyId:guid}/instance-upload/register")]
        [RequiresModule(ModuleConstants.Pacs)]
        public async Task<IActionResult> RegisterStudyInstanceUpload(Guid studyId, [FromBody] StudyInstanceRegisterRequest request)
        {
            try
            {
                if (request == null || request.AssetId == Guid.Empty)
                    return BadRequest(new { success = false, error = "AssetId is required." });
                if (request.InstanceUrls == null || request.InstanceUrls.Count == 0)
                    return BadRequest(new { success = false, error = "At least one staged instance URL is required." });

                var (study, error) = await LoadStudyForWriteAsync(studyId);
                if (error != null) return error;

                var stagingPrefix = $"{study!.HospitalId:N}/{study.Id:N}/staging/{request.AssetId:N}/";
                foreach (var url in request.InstanceUrls)
                {
                    var p = BlobPathFromUrl(url, "dicom-files");
                    if (p == null || !p.Replace('\\', '/').TrimStart('/')
                            .StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return BadRequest(new { success = false, error = "One or more instance URLs do not belong to this study's staging area." });
                    }
                }

                var sampleUrls = new List<string> { request.InstanceUrls[0] };
                if (request.InstanceUrls.Count > 1)
                    sampleUrls.Add(request.InstanceUrls[request.InstanceUrls.Count - 1]);
                foreach (var url in sampleUrls)
                {
                    var path = BlobPathFromUrl(url, "dicom-files");
                    if (path == null || !await _blobService.BlobExistsAsync(path, "dicom-files"))
                        return BadRequest(new { success = false, error = "Staged instance blob not found in Azure — re-upload the instances and retry." });
                }

                var manifestJson = System.Text.Json.JsonSerializer.Serialize(new { Instances = request.InstanceUrls });
                var manifestPath = $"{study.HospitalId:N}/{study.Id:N}/staging/{request.AssetId:N}/_manifest.json";
                string manifestUrl;
                using (var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifestJson)))
                {
                    manifestUrl = await _blobService.UploadFileAtPathAsync(ms, manifestPath, "application/json", "dicom-files");
                }

                var asset = await _context.StudyAssets
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(a => a.Id == request.AssetId);
                if (asset == null)
                {
                    asset = new StudyAsset
                    {
                        Id = request.AssetId,
                        ImagingStudyId = study.Id,
                        AppointmentId = null,
                        BlobUrl = manifestUrl,
                        FileName = $"study_{request.InstanceUrls.Count}_instances",
                        FileType = "instances",
                        UploadedAt = DateTime.UtcNow,
                        HospitalId = study.HospitalId,
                        ExtractionStatus = "Queued",
                    };
                    _context.StudyAssets.Add(asset);
                }
                else
                {
                    asset.BlobUrl = manifestUrl;
                    asset.FileType = "instances";
                    asset.UploadedAt = DateTime.UtcNow;
                    asset.ExtractionStatus = "Queued";
                    asset.ImagingStudyId = study.Id;
                }

                await _context.SaveChangesAsync(default);
                _extractionQueue.Enqueue(asset.Id);

                return Ok(new { success = true, data = asset });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"STUDY_INSTANCE_REGISTER_FAILURE: {ex.Message}" });
            }
        }

        // ── Study browser / inbox / assign (PACS-only worklist) ─────────────

        [HttpGet("studies")]
        [RequiresModule(ModuleConstants.Pacs)]
        public async Task<IActionResult> ListStudies(
            [FromQuery] string? status = null,
            [FromQuery] bool? assigned = null,
            [FromQuery] string? modality = null,
            [FromQuery] string? q = null,
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] string? sortBy = null,
            [FromQuery] string? sortDir = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            try
            {
                if (page < 1) page = 1;
                pageSize = Math.Clamp(pageSize, 1, 200);

                // Tenant-scoped by the global query filter (HospitalId == active center).
                var query = _context.ImagingStudies.AsNoTracking();

                if (!string.IsNullOrWhiteSpace(status))
                    query = query.Where(s => s.Status == status);
                if (!string.IsNullOrWhiteSpace(modality))
                    query = query.Where(s => s.Modality == modality);
                // Date filter on the study date, falling back to upload time for
                // legacy rows without a DICOM study date. `to` is inclusive
                // (covers the whole day).
                if (from.HasValue)
                {
                    var f = from.Value.Date;
                    query = query.Where(s => (s.StudyDate ?? s.CreatedAt) >= f);
                }
                if (to.HasValue)
                {
                    var t = to.Value.Date.AddDays(1);
                    query = query.Where(s => (s.StudyDate ?? s.CreatedAt) < t);
                }
                // The inbox is studies linked to neither a patient nor an
                // appointment — robust regardless of MatchStatus, so RIS+PACS
                // appointment-linked studies are never wrongly inboxed.
                if (assigned == false)
                    query = query.Where(s => s.AppointmentId == null && s.PatientId == null);
                else if (assigned == true)
                    query = query.Where(s => s.AppointmentId != null || s.PatientId != null);
                if (!string.IsNullOrWhiteSpace(q))
                {
                    var term = q.Trim();
                    query = query.Where(s =>
                        (s.PatientName != null && s.PatientName.Contains(term)) ||
                        (s.DicomPatientId != null && s.DicomPatientId.Contains(term)) ||
                        (s.AccessionNumber != null && s.AccessionNumber.Contains(term)));
                }

                var total = await query.CountAsync();

                // Column sorting. `size` orders by the per-study StorageBytes sum
                // (a correlated sum — runs only when the user explicitly sorts by
                // it). Everything else is a plain column. Default: newest first.
                var asc = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);
                var ordered = (sortBy?.ToLowerInvariant()) switch
                {
                    "patient" or "patientname" => asc ? query.OrderBy(s => s.PatientName) : query.OrderByDescending(s => s.PatientName),
                    "modality" => asc ? query.OrderBy(s => s.Modality) : query.OrderByDescending(s => s.Modality),
                    "studydate" or "date" => asc ? query.OrderBy(s => s.StudyDate) : query.OrderByDescending(s => s.StudyDate),
                    "accession" or "accessionnumber" => asc ? query.OrderBy(s => s.AccessionNumber) : query.OrderByDescending(s => s.AccessionNumber),
                    "status" => asc ? query.OrderBy(s => s.Status) : query.OrderByDescending(s => s.Status),
                    "size" or "sizebytes" => asc
                        ? query.OrderBy(s => _context.StudyAssets.Where(a => a.ImagingStudyId == s.Id).Sum(a => (long?)a.StorageBytes) ?? 0)
                        : query.OrderByDescending(s => _context.StudyAssets.Where(a => a.ImagingStudyId == s.Id).Sum(a => (long?)a.StorageBytes) ?? 0),
                    _ => asc ? query.OrderBy(s => s.CreatedAt) : query.OrderByDescending(s => s.CreatedAt),
                };

                // Fetch the page first, then get all asset counts in ONE grouped
                // query (was a correlated subquery per row — N counts per page).
                var pageRows = await ordered
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Select(s => new
                    {
                        s.Id,
                        s.StudyInstanceUID,
                        s.PatientName,
                        s.DicomPatientId,
                        s.AccessionNumber,
                        s.Modality,
                        s.StudyDate,
                        s.StudyDescription,
                        s.Status,
                        s.MatchStatus,
                        s.AppointmentId,
                        s.PatientId,
                        s.CreatedAt,
                    })
                    .ToListAsync();

                // One asset query for the page → count, total size, and the
                // failure reason (so the worklist can show WHY a study failed
                // and how much storage it uses), aggregated in memory.
                var pageIds = pageRows.Select(r => r.Id).ToList();
                var pageAssets = await _context.StudyAssets
                    .Where(a => a.ImagingStudyId != null && pageIds.Contains(a.ImagingStudyId.Value))
                    .Select(a => new { StudyId = a.ImagingStudyId!.Value, a.StorageBytes, a.ExtractionStatus, a.ExtractionError,
                                       a.ExtractionPhase, a.ExtractionProcessedSlices, a.ExtractionTotalSlices })
                    .ToListAsync();
                var byStudy = pageAssets
                    .GroupBy(a => a.StudyId)
                    .ToDictionary(g => g.Key, g => new
                    {
                        Count = g.Count(),
                        Size = g.Sum(x => x.StorageBytes),
                        Error = g.Where(x => x.ExtractionStatus == "Failed" && !string.IsNullOrWhiteSpace(x.ExtractionError))
                                 .Select(x => x.ExtractionError).FirstOrDefault(),
                        // Live extraction progress (summed across the study's assets)
                        // so the worklist can show a loader + % per processing study.
                        Phase     = g.Select(x => x.ExtractionPhase).FirstOrDefault(p => !string.IsNullOrEmpty(p)),
                        Processed = g.Sum(x => x.ExtractionProcessedSlices),
                        Total     = g.Sum(x => x.ExtractionTotalSlices),
                    });

                // Report status per study (None / Draft / Finalized). A report
                // links to a study by ImagingStudyId (PACS-only) OR AppointmentId
                // (appointment-linked), so match either. The global hospital query
                // filter scopes these to the current centre automatically.
                var pageApptIds = pageRows.Where(r => r.AppointmentId != null)
                                          .Select(r => r.AppointmentId!.Value).Distinct().ToList();
                var reportRows = await _context.DiagnosticReports
                    .Where(r => (r.ImagingStudyId != null && pageIds.Contains(r.ImagingStudyId.Value))
                             || (r.AppointmentId != null && pageApptIds.Contains(r.AppointmentId.Value)))
                    .Select(r => new { r.ImagingStudyId, r.AppointmentId, r.IsFinalized, r.Status })
                    .ToListAsync();

                var items = pageRows.Select(s =>
                {
                    byStudy.TryGetValue(s.Id, out var agg);
                    // Sign-off precedence: Final/Addended > Preliminary > Draft.
                    // (IsFinalized is the back-compat shim — true only for
                    // Final/Addended — so old rows without a Status still resolve.)
                    var studyReports = reportRows.Where(r =>
                        r.ImagingStudyId == s.Id ||
                        (s.AppointmentId != null && r.AppointmentId == s.AppointmentId.Value)).ToList();
                    var reportStatus =
                        studyReports.Count == 0 ? "None"
                        : studyReports.Any(r => r.IsFinalized
                                                || r.Status == "Final" || r.Status == "Addended") ? "Finalized"
                        : studyReports.Any(r => r.Status == "Preliminary") ? "Preliminary"
                        : "Draft";
                    return new
                    {
                        imagingStudyId = s.Id,
                        s.StudyInstanceUID,
                        s.PatientName,
                        s.DicomPatientId,
                        s.AccessionNumber,
                        s.Modality,
                        s.StudyDate,
                        s.StudyDescription,
                        s.Status,
                        s.MatchStatus,
                        s.AppointmentId,
                        s.PatientId,
                        s.CreatedAt,
                        assetCount = agg?.Count ?? 0,
                        sizeBytes = agg?.Size ?? 0L,
                        reportStatus, // "None" | "Draft" | "Finalized" — drives the worklist report badge
                        extractionError = string.Equals(s.Status, "Failed", StringComparison.OrdinalIgnoreCase) ? agg?.Error : null,
                        // Live progress for the loader + % shown on processing rows.
                        progressPhase = agg?.Phase,
                        progressProcessed = agg?.Processed ?? 0,
                        progressTotal = agg?.Total ?? 0,
                        progressPercent = (agg?.Total ?? 0) > 0
                            ? (int)Math.Round(100.0 * (agg?.Processed ?? 0) / agg!.Total)
                            : 0,
                    };
                }).ToList();

                // Whole-centre storage usage so the worklist can show a meter.
                long usedBytes = 0; long? quotaBytes = null;
                try
                {
                    var usage = await _storage.GetUsageAsync(_userContext.HospitalId);
                    usedBytes = usage.UsedBytes;
                    quotaBytes = usage.IncludedBytes;
                }
                catch { /* metering is best-effort for the list header */ }

                return Ok(new { success = true, data = new { total, page, pageSize, items, usedBytes, quotaBytes } });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"STUDY_LIST_FAILURE: {ex.Message}" });
            }
        }

        [HttpGet("studies/{studyId:guid}")]
        [RequiresModule(ModuleConstants.Pacs)]
        public async Task<IActionResult> GetStudyDetail(Guid studyId)
        {
            try
            {
                // Tenant-scoped by the global query filter.
                var study = await _context.ImagingStudies
                    .FirstOrDefaultAsync(s => s.Id == studyId);
                if (study == null)
                    return NotFound(new { success = false, error = "Imaging study not found." });

                var assets = await _context.StudyAssets
                    .Where(a => a.ImagingStudyId == study.Id)
                    .Include(a => a.Slices)
                    .OrderByDescending(a => a.UploadedAt)
                    .ToListAsync();

                return Ok(new
                {
                    success = true,
                    data = new
                    {
                        imagingStudyId = study.Id,
                        study.StudyInstanceUID,
                        study.PatientName,
                        study.DicomPatientId,
                        study.AccessionNumber,
                        study.Modality,
                        study.StudyDate,
                        study.StudyDescription,
                        study.Status,
                        study.MatchStatus,
                        study.AppointmentId,
                        study.PatientId,
                        study.CreatedAt,
                        assets = BuildManifestAssetDtos(assets),
                    },
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"STUDY_DETAIL_FAILURE: {ex.Message}" });
            }
        }

        [HttpPost("studies/{studyId:guid}/assign")]
        [RequiresModule(ModuleConstants.Pacs)]
        public async Task<IActionResult> AssignStudy(Guid studyId, [FromBody] StudyAssignRequest request)
        {
            try
            {
                if (request == null || (request.PatientId == null && request.AppointmentId == null))
                    return BadRequest(new { success = false, error = "Provide a PatientId and/or an AppointmentId." });

                var (study, error) = await LoadStudyForWriteAsync(studyId);
                if (error != null) return error;

                if (request.AppointmentId is Guid aid && aid != Guid.Empty)
                {
                    var appt = await _context.Appointments
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(a => a.AppointmentId == aid);
                    if (appt == null)
                        return NotFound(new { success = false, error = "Appointment not found." });
                    var apptBlock = EnsureHospitalAccess(appt.HospitalId);
                    if (apptBlock != null) return apptBlock;

                    study!.AppointmentId = appt.AppointmentId;
                    if (request.PatientId == null && appt.PatientId != Guid.Empty)
                        study.PatientId = appt.PatientId;
                }

                if (request.PatientId is Guid pid && pid != Guid.Empty)
                {
                    var patient = await _context.Patients
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(p => p.PatientId == pid);
                    if (patient == null)
                        return NotFound(new { success = false, error = "Patient not found." });
                    var pBlock = EnsureHospitalAccess(patient.HospitalId);
                    if (pBlock != null) return pBlock;

                    study!.PatientId = patient.PatientId;
                }

                study!.MatchStatus = ImagingStudyMatchStatus.ManuallyAssigned;
                await _context.SaveChangesAsync(default);

                return Ok(new
                {
                    success = true,
                    data = new { imagingStudyId = study.Id, study.PatientId, study.AppointmentId, study.MatchStatus },
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"STUDY_ASSIGN_FAILURE: {ex.Message}" });
            }
        }

        [HttpDelete("studies/{studyId:guid}")]
        [RequiresModule(ModuleConstants.Pacs)]
        public async Task<IActionResult> DeleteStudy(Guid studyId)
        {
            try
            {
                var (study, error) = await LoadStudyForWriteAsync(studyId);
                if (error != null) return error;

                // Only PACS-only studies are deletable here. A study linked to an
                // appointment shares its assets with that visit (asset carries both
                // FKs), so deleting it would wipe the visit's imaging — manage those
                // through the appointment instead.
                if (study!.AppointmentId != null)
                    return BadRequest(new { success = false, error = "This study is linked to an appointment; manage its imaging from the appointment instead." });

                var assets = await _context.StudyAssets
                    .IgnoreQueryFilters()
                    .Where(a => a.ImagingStudyId == study.Id)
                    .Include(a => a.Slices)
                    .ToListAsync();

                // Every blob this study owns: original assets + extracted slices +
                // thumbnails. Stored URLs are raw blob URLs (not CDN), so deleting
                // by URL is precise regardless of the folder convention.
                var urls = new List<string>();
                foreach (var a in assets)
                {
                    if (!string.IsNullOrWhiteSpace(a.BlobUrl)) urls.Add(a.BlobUrl);
                    foreach (var s in a.Slices)
                    {
                        if (!string.IsNullOrWhiteSpace(s.BlobUrl))
                        {
                            urls.Add(s.BlobUrl);
                            // Raw HTJ2K progressive frame lives at the slice path
                            // with a .jhc extension (best-effort: not every slice
                            // has one; a missing-blob delete is a harmless no-op).
                            var frame = FrameUrlFromSlice(s.BlobUrl);
                            if (frame != null) urls.Add(frame);
                            // Progressive preview JPEG sits beside the slice (_prev.jpg).
                            var preview = _1Rad.Infrastructure.Services.DicomExtractionService.PreviewUrlFromSlice(s.BlobUrl);
                            if (preview != null) urls.Add(preview);
                        }
                        if (!string.IsNullOrWhiteSpace(s.ThumbnailUrl)) urls.Add(s.ThumbnailUrl);
                    }
                }

                // Best-effort blob deletion with bounded concurrency. A transient
                // blob failure must not block the DB cleanup (a re-run finishes it).
                var deleted = 0;
                for (int i = 0; i < urls.Count; i += 16)
                {
                    var batch = urls.Skip(i).Take(16).Select(async u =>
                    {
                        try { await _blobService.DeleteFileAsync(u, "dicom-files"); System.Threading.Interlocked.Increment(ref deleted); }
                        catch { /* best-effort; orphaned blob is harmless */ }
                    });
                    await Task.WhenAll(batch);
                }

                // DB cleanup: study-based reports (NoAction FK) → assets (cascade
                // deletes their slices) → the study itself.
                var reports = await _context.DiagnosticReports
                    .IgnoreQueryFilters()
                    .Where(r => r.ImagingStudyId == study.Id)
                    .ToListAsync();
                if (reports.Count > 0) _context.DiagnosticReports.RemoveRange(reports);
                _context.StudyAssets.RemoveRange(assets);
                _context.ImagingStudies.Remove(study);
                await _context.SaveChangesAsync(default);
                _storage.Invalidate(study.HospitalId);

                return Ok(new
                {
                    success = true,
                    data = new { imagingStudyId = study.Id, deletedAssets = assets.Count, deletedBlobs = deleted },
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"STUDY_DELETE_FAILURE: {ex.Message}" });
            }
        }

        // Export = the original uploaded files for download (e.g. before a
        // post-grace auto-delete). A GET, so it stays available during the PACS
        // read-only grace window.
        [HttpGet("studies/{studyId:guid}/export")]
        [RequiresModule(ModuleConstants.Pacs)]
        public async Task<IActionResult> ExportStudy(Guid studyId)
        {
            try
            {
                var study = await _context.ImagingStudies
                    .FirstOrDefaultAsync(s => s.Id == studyId);
                if (study == null)
                    return NotFound(new { success = false, error = "Imaging study not found." });

                var assets = await _context.StudyAssets
                    .Where(a => a.ImagingStudyId == study.Id)
                    .OrderByDescending(a => a.UploadedAt)
                    .ToListAsync();

                var files = assets.Select(a => new
                {
                    assetId = a.Id,
                    fileName = a.FileName,
                    fileType = a.FileType,
                    downloadUrl = ToCdn(a.BlobUrl),
                }).ToList();

                return Ok(new
                {
                    success = true,
                    data = new { imagingStudyId = study.Id, patientName = study.PatientName, files },
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"STUDY_EXPORT_FAILURE: {ex.Message}" });
            }
        }

        [HttpGet("proxy-asset")]
        [HttpHead("proxy-asset")]
        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        public async Task<IActionResult> ProxyAsset([FromQuery] string url, [FromQuery] long? exp, [FromQuery] string? sig)
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

                // Authorization. This endpoint is [AllowAnonymous] because the
                // DICOM viewer fetches slices with no Bearer and the public
                // tracking page renders branding with no session — but it must
                // NOT be an open account-wide reader. A request is authorized if:
                //   • it carries a valid signature for this exact blob (the
                //     capability the backend minted for an already-authorized
                //     viewer), OR
                //   • it carries a Bearer whose user is entitled to the blob's
                //     hospital (the apiClient download/fallback paths), OR
                //   • the blob is in an intentionally-public branding container.
                // Anything else (anonymous, no signature, PHI container) is denied.
                var container = ContainerOf(url);
                var signed = exp.HasValue && !string.IsNullOrEmpty(sig) && _signer.Validate(url, exp.Value, sig);
                var open = OpenProxyContainers.Contains(container, StringComparer.OrdinalIgnoreCase);
                if (!(signed || open || IsBearerEntitledToBlob(url)))
                    return StatusCode(StatusCodes.Status403Forbidden, "Asset access requires a valid signature or authorization.");

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

    public class InstanceUploadSasRequest
    {
        public Guid AppointmentId { get; set; }
        public int Count { get; set; }
    }

    public class InstanceUploadRegisterRequest
    {
        public Guid AssetId { get; set; }
        public Guid AppointmentId { get; set; }
        public Guid? AppointmentServiceId { get; set; }
        // The PublicReadUrls of the staged instance blobs the bridge uploaded.
        public List<string> InstanceUrls { get; set; } = new();
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

    // ── Cloud PACS-only (appointment-free) ingestion DTOs ───────────────────

    public class StudyRegisterRequest
    {
        // Supplied by the web Upload Center at upload time; null for bridge
        // pushes (extraction discovers it later and refines the study).
        public string? StudyInstanceUID { get; set; }
        public string? PatientName { get; set; }
        public string? DicomPatientId { get; set; }
        public string? AccessionNumber { get; set; }
        public string? Modality { get; set; }
        public DateTime? StudyDate { get; set; }
        public string? StudyDescription { get; set; }
        // web-upload | bridge | api-upload. Defaults to web-upload.
        public string? Source { get; set; }
    }

    public class StudyUploadTokenRequest
    {
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string? ContentType { get; set; }
    }

    public class StudyUploadCompleteRequest
    {
        public Guid AssetId { get; set; }
        public string BlobPath { get; set; } = string.Empty;
        public string ContainerName { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long? ActualSize { get; set; }
    }

    public class StudyInstanceSasRequest
    {
        public int Count { get; set; }
    }

    public class StudyInstanceRegisterRequest
    {
        public Guid AssetId { get; set; }
        public List<string> InstanceUrls { get; set; } = new();
    }

    public class StudyAssignRequest
    {
        public Guid? PatientId { get; set; }
        public Guid? AppointmentId { get; set; }
    }
}
