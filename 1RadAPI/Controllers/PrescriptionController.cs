using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;

namespace _1RadAPI.Controllers
{
    [Route("api/v1/[controller]")]
    [ApiController]
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

        [HttpGet("{doctorId}")]
        public async Task<IActionResult> GetProtocol(Guid doctorId)
        {
            try
            {
                var hospitalId = _userContext.HospitalId;
                var protocol = await _context.PrescriptionProtocols
                    .FirstOrDefaultAsync(p => p.DoctorId == doctorId && p.HospitalId == hospitalId);

                if (protocol == null) 
                {
                    return Ok(new { success = true, data = (object?)null, message = "No custom branding profile found. Using system defaults." });
                }

                return Ok(new { success = true, data = protocol });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"PROTOCOL RETRIEVAL FAILURE: {ex.Message}" });
            }
        }

        [HttpPost]
        public async Task<IActionResult> SaveProtocol([FromForm] PrescriptionRequest request)
        {
            try
            {
                var hospitalId = _userContext.HospitalId;

                // STRATEGIC VALIDATION
                if (request.HeaderMargin < 8 || request.LeftMargin < 8 || request.RightMargin < 8 || request.BottomMargin < 8)
                {
                    return BadRequest(new { success = false, error = "GEOMETRIC FAILURE: All margins must be at least 8mm for clinical compliance." });
                }

                if (request.FontSize < 8)
                {
                    return BadRequest(new { success = false, error = "TYPOGRAPHIC FAILURE: Font size must be at least 8px for readability standards." });
                }

                var protocol = await _context.PrescriptionProtocols
                    .FirstOrDefaultAsync(p => p.DoctorId == request.DoctorId && p.HospitalId == hospitalId);

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

                if (protocol == null)
                {
                    protocol = new PrescriptionProtocol
                    {
                        Id = Guid.NewGuid(),
                        DoctorId = request.DoctorId,
                        HospitalId = hospitalId,
                        HeaderMargin = request.HeaderMargin,
                        LeftMargin = request.LeftMargin,
                        RightMargin = request.RightMargin,
                        BottomMargin = request.BottomMargin,
                        FontSize = request.FontSize,
                        FontColor = request.FontColor,
                        FontFamily = request.FontFamily,
                        LetterheadBlobUrl = letterheadUrl,
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
                    protocol.UpdatedAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync(default);

                return Ok(new { success = true, data = protocol });
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
        public float HeaderMargin { get; set; }
        public float LeftMargin { get; set; }
        public float RightMargin { get; set; }
        public float BottomMargin { get; set; }
        public int FontSize { get; set; }
        public string? FontColor { get; set; }
        public string? FontFamily { get; set; }
        public IFormFile? LetterheadFile { get; set; } = null;
    }
}
