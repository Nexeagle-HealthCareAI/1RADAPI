using _1Rad.Application.Interfaces;
using _1Rad.Domain.Constants;
using _1Rad.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace _1RadAPI.Controllers
{
    [Route("api/v1/[controller]")]
    [ApiController]
    [Authorize] // All endpoints require a valid JWT. Hospital isolation is enforced via HospitalId claim in the DB query.
    // The report letterhead/margins apply to EVERY SKU's reports (RIS and Cloud
    // PACS both generate reports), so this is not RIS-gated. The setting is now a
    // single per-centre protocol shared by all doctors (no per-doctor variation).
    public class PrescriptionController : ControllerBase
    {
        private readonly IApplicationDbContext _context;
        private readonly IBlobService _blobService;
        private readonly IUserContext _userContext;

        public PrescriptionController(IApplicationDbContext context, IBlobService blobService, IUserContext userContext)
        {
            _context = context;
            _blobService = blobService;
            _userContext = userContext;
        }

        [HttpGet("me")]
        public async Task<IActionResult> GetMyProtocol()
        {
            return await GetProtocol(_userContext.UserId);
        }

        // The {doctorId} route is retained for client compatibility (ReportingPage
        // still calls /Prescription/{doctorId} for the report's author), but the
        // protocol is now SINGLE PER CENTRE — the same letterhead/margins for every
        // doctor — so the id is ignored and we always return the hospital's one
        // protocol. The ordering keeps a deterministic answer during the transition
        // window where a centre might still have stale per-doctor rows (preferring
        // the one with a letterhead, then the earliest = the centre's original setup).
        [HttpGet("{doctorId:guid}")]
        public async Task<IActionResult> GetProtocol(Guid doctorId)
        {
            try
            {
                var hospitalId = _userContext.HospitalId;
                var protocol = await _context.PrescriptionProtocols
                    .AsNoTracking()
                    .Where(p => p.HospitalId == hospitalId)
                    .OrderByDescending(p => p.LetterheadBlobUrl != null)
                    .ThenBy(p => p.CreatedAt)
                    .FirstOrDefaultAsync();

                // Centre details for the STANDARD design (and the PDF header in
                // LETTERHEAD mode) — read from the Hospital record so there is
                // one place to correct the name / address / registration.
                var centre = await _context.Hospitals
                    .AsNoTracking()
                    .Where(h => h.HospitalId == hospitalId)
                    .Select(h => new
                    {
                        name = h.HospitalName,
                        address = h.HospitalAddress,
                        phone = h.PublicContact,
                        email = h.PublicEmail,
                        registrationNumber = h.RegistrationNumber,
                        nabhNumber = h.NABHNumber,
                    })
                    .FirstOrDefaultAsync();

                if (protocol == null)
                {
                    // No setup yet: the standard design, built from the centre.
                    return Ok(new
                    {
                        success = true,
                        data = (object?)null,
                        defaults = new { paperMode = PaperModes.Standard, showRegistration = true, showQr = true, pdfStandardHeader = true },
                        centre,
                        message = "No custom branding profile found. Using system defaults."
                    });
                }

                // A flat projection (not the entity) so the response never
                // carries the Doctor user record.
                return Ok(new
                {
                    success = true,
                    data = new
                    {
                        protocol.Id,
                        protocol.HospitalId,
                        protocol.HeaderMargin,
                        protocol.LeftMargin,
                        protocol.RightMargin,
                        protocol.BottomMargin,
                        protocol.FontSize,
                        protocol.FontColor,
                        protocol.FontFamily,
                        protocol.LetterheadBlobUrl,
                        protocol.OverflowBackgroundMode,
                        protocol.PaperMode,
                        protocol.HeaderImageBlobUrl,
                        protocol.FooterImageBlobUrl,
                        protocol.LogoBlobUrl,
                        protocol.AccentColor,
                        protocol.ShowRegistration,
                        protocol.ShowQr,
                        protocol.PdfStandardHeader,
                        protocol.UpdatedAt,
                        centre,
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"PROTOCOL RETRIEVAL FAILURE: {ex.Message}" });
            }
        }

        /// <summary>
        /// Create or update a doctor's prescription layout protocol.
        /// Restricted to AdminDoctor, Admin, and Doctor roles only.
        /// </summary>
        [HttpPost]
        [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Doctor}")]
        public async Task<IActionResult> SaveProtocol([FromForm] PrescriptionRequest request)
        {
            try
            {
                var hospitalId = _userContext.HospitalId;

                var paperMode = string.IsNullOrWhiteSpace(request.PaperMode)
                    ? null
                    : request.PaperMode.Trim().ToUpperInvariant();
                if (paperMode != null && !PaperModes.IsValid(paperMode))
                {
                    return BadRequest(new { success = false, error = "Paper mode must be IMAGES, STANDARD or LETTERHEAD." });
                }
                if (request.AccentColor != null && !System.Text.RegularExpressions.Regex.IsMatch(request.AccentColor, "^#[0-9A-Fa-f]{6}$"))
                {
                    return BadRequest(new { success = false, error = "Colour must look like #0F52BA." });
                }

                // STRATEGIC VALIDATION
                if (request.HeaderMargin < 8 || request.LeftMargin < 8 || request.RightMargin < 8 || request.BottomMargin < 8)
                {
                    return BadRequest(new { success = false, error = "GEOMETRIC FAILURE: All margins must be at least 8mm for clinical compliance." });
                }

                if (request.FontSize < 8)
                {
                    return BadRequest(new { success = false, error = "TYPOGRAPHIC FAILURE: Font size must be at least 8px for readability standards." });
                }

                // Single per-centre protocol: upsert the hospital's one row,
                // regardless of which doctor is saving (request.DoctorId is ignored
                // now). The ordering matches GetProtocol so we update the SAME row
                // the centre reads, even if stale per-doctor rows still linger
                // pre-migration.
                var protocol = await _context.PrescriptionProtocols
                    .Where(p => p.HospitalId == hospitalId)
                    .OrderByDescending(p => p.LetterheadBlobUrl != null)
                    .ThenBy(p => p.CreatedAt)
                    .FirstOrDefaultAsync();

                string? letterheadUrl = protocol?.LetterheadBlobUrl;

                if (request.LetterheadFile != null)
                {
                    try
                    {
                        using var stream = request.LetterheadFile.OpenReadStream();
                        letterheadUrl = await _blobService.UploadFileAsync(stream, request.LetterheadFile.FileName, request.LetterheadFile.ContentType);
                    }
                    catch (Exception ex)
                    {
                        return StatusCode(500, new { success = false, error = $"ASSET UPLOAD FAILURE: Could not synchronize institutional letterhead. {ex.Message}" });
                    }
                }

                // Report-paper pictures: a new file replaces the old one; a
                // Remove* flag clears it; otherwise the stored one is kept.
                async Task<string?> UploadOrKeep(IFormFile? file, bool remove, string? current)
                {
                    if (file != null)
                    {
                        if (file.Length > 8 * 1024 * 1024)
                            throw new InvalidOperationException($"{file.FileName} is larger than 8 MB.");
                        if (!(file.ContentType?.StartsWith("image/") ?? false))
                            throw new InvalidOperationException($"{file.FileName} must be a picture (PNG or JPG).");
                        using var s = file.OpenReadStream();
                        return await _blobService.UploadFileAsync(s, file.FileName, file.ContentType);
                    }
                    return remove ? null : current;
                }

                string? headerUrl, footerUrl, logoUrl;
                try
                {
                    headerUrl = await UploadOrKeep(request.HeaderImageFile, request.RemoveHeaderImage, protocol?.HeaderImageBlobUrl);
                    footerUrl = await UploadOrKeep(request.FooterImageFile, request.RemoveFooterImage, protocol?.FooterImageBlobUrl);
                    logoUrl = await UploadOrKeep(request.LogoFile, request.RemoveLogo, protocol?.LogoBlobUrl);
                }
                catch (InvalidOperationException ex)
                {
                    return BadRequest(new { success = false, error = ex.Message });
                }
                catch (Exception ex)
                {
                    return StatusCode(500, new { success = false, error = $"Could not upload the picture. {ex.Message}" });
                }
                if (request.RemoveLetterhead && request.LetterheadFile == null) letterheadUrl = null;

                paperMode ??= protocol?.PaperMode ?? PaperModes.Letterhead;

                if (paperMode == PaperModes.Images && (headerUrl == null || footerUrl == null))
                {
                    return BadRequest(new { success = false, error = "Upload both a header picture and a footer picture, or choose another way." });
                }

                if (protocol == null)
                {
                    protocol = new PrescriptionProtocol
                    {
                        Id = Guid.NewGuid(),
                        // DoctorId is now just provenance (the setting is centre-wide).
                        // Stamp the saver when the client doesn't send a valid id so
                        // the non-null FK to Users always resolves.
                        DoctorId = request.DoctorId == Guid.Empty ? _userContext.UserId : request.DoctorId,
                        HospitalId = hospitalId,
                        HeaderMargin = request.HeaderMargin,
                        LeftMargin = request.LeftMargin,
                        RightMargin = request.RightMargin,
                        BottomMargin = request.BottomMargin,
                        FontSize = request.FontSize,
                        FontColor = request.FontColor,
                        FontFamily = request.FontFamily,
                        LetterheadBlobUrl = letterheadUrl,
                        OverflowBackgroundMode = request.OverflowBackgroundMode ?? "REUSE",
                        PaperMode = paperMode,
                        HeaderImageBlobUrl = headerUrl,
                        FooterImageBlobUrl = footerUrl,
                        LogoBlobUrl = logoUrl,
                        AccentColor = request.AccentColor,
                        ShowRegistration = request.ShowRegistration ?? true,
                        ShowQr = request.ShowQr ?? true,
                        PdfStandardHeader = request.PdfStandardHeader ?? true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _context.PrescriptionProtocols.Add(protocol);
                }
                else
                {
                    protocol.HeaderMargin = request.HeaderMargin;
                    protocol.LeftMargin = request.LeftMargin;
                    protocol.RightMargin = request.RightMargin;
                    protocol.BottomMargin = request.BottomMargin;
                    protocol.FontSize = request.FontSize;
                    protocol.FontColor = request.FontColor;
                    protocol.FontFamily = request.FontFamily;
                    protocol.LetterheadBlobUrl = letterheadUrl;
                    protocol.OverflowBackgroundMode = request.OverflowBackgroundMode ?? "REUSE";
                    protocol.PaperMode = paperMode;
                    protocol.HeaderImageBlobUrl = headerUrl;
                    protocol.FooterImageBlobUrl = footerUrl;
                    protocol.LogoBlobUrl = logoUrl;
                    // Old clients (no paper fields) keep the stored values.
                    if (request.AccentColor != null) protocol.AccentColor = request.AccentColor;
                    if (request.ShowRegistration.HasValue) protocol.ShowRegistration = request.ShowRegistration.Value;
                    if (request.ShowQr.HasValue) protocol.ShowQr = request.ShowQr.Value;
                    if (request.PdfStandardHeader.HasValue) protocol.PdfStandardHeader = request.PdfStandardHeader.Value;
                    protocol.UpdatedAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync(default);

                return await GetProtocol(protocol.DoctorId);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"STRATEGIC SYNC FAILURE: {ex.Message}" });
            }
        }
    }

    public class PrescriptionRequest
    {
        public Guid DoctorId { get; set; }
        public decimal HeaderMargin { get; set; }
        public decimal LeftMargin { get; set; }
        public decimal RightMargin { get; set; }
        public decimal BottomMargin { get; set; }
        public int FontSize { get; set; }
        public string? FontColor { get; set; }
        public string? FontFamily { get; set; }
        public string? OverflowBackgroundMode { get; set; } // 'REUSE' or 'BLANK'
        public IFormFile? LetterheadFile { get; set; } = null;
        public bool RemoveLetterhead { get; set; }

        // Report paper (migration 111). All optional: an older client that
        // sends none of these keeps the centre's stored paper settings.
        public string? PaperMode { get; set; } // IMAGES | STANDARD | LETTERHEAD
        public IFormFile? HeaderImageFile { get; set; }
        public IFormFile? FooterImageFile { get; set; }
        public IFormFile? LogoFile { get; set; }
        public bool RemoveHeaderImage { get; set; }
        public bool RemoveFooterImage { get; set; }
        public bool RemoveLogo { get; set; }
        public string? AccentColor { get; set; }
        public bool? ShowRegistration { get; set; }
        public bool? ShowQr { get; set; }
        public bool? PdfStandardHeader { get; set; }
    }
}
