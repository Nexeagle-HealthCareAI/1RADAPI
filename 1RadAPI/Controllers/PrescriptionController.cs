using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;

namespace _1RadAPI.Controllers
{
    [Route("api/[controller]")]
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
            var hospitalId = _userContext.HospitalId;
            var protocol = await _context.PrescriptionProtocols
                .FirstOrDefaultAsync(p => p.DoctorId == doctorId && p.HospitalId == hospitalId);

            if (protocol == null) return NotFound();

            return Ok(protocol);
        }

        [HttpPost]
        public async Task<IActionResult> SaveProtocol([FromForm] PrescriptionRequest request)
        {
            var hospitalId = _userContext.HospitalId;

            // STRATEGIC VALIDATION
            if (request.HeaderMargin < 8 || request.LeftMargin < 8 || request.RightMargin < 8 || request.BottomMargin < 8)
            {
                return BadRequest("GEOMETRIC FAILURE: All margins must be at least 8mm for clinical compliance.");
            }

            if (request.FontSize < 8)
            {
                return BadRequest("TYPOGRAPHIC FAILURE: Font size must be at least 8px for readability standards.");
            }

            var protocol = await _context.PrescriptionProtocols
                .FirstOrDefaultAsync(p => p.DoctorId == request.DoctorId && p.HospitalId == hospitalId);

            string letterheadUrl = protocol?.LetterheadBlobUrl;

            if (request.LetterheadFile != null)
            {
                // Delete old one if exists
                if (!string.IsNullOrEmpty(letterheadUrl))
                {
                    await _blobService.DeleteFileAsync(letterheadUrl);
                }

                using var stream = request.LetterheadFile.OpenReadStream();
                letterheadUrl = await _blobService.UploadFileAsync(
                    stream, 
                    request.LetterheadFile.FileName, 
                    request.LetterheadFile.ContentType
                );
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

            return Ok(protocol);
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
        public string FontColor { get; set; }
        public string FontFamily { get; set; }
        public IFormFile LetterheadFile { get; set; }
    }
}
