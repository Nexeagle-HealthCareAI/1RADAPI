using System.Text.Json;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Constants;
using _1Rad.Domain.Entities;
using _1RadAPI.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace _1RadAPI.Controllers;

/// <summary>
/// Viewer measurements that survive a reload (gap analysis P3-7). Before Oct 2026,
/// lengths, angles and ROIs drawn in the viewer were lost on refresh.
///
/// GET  api/v1/study-annotations?key=appointment:{guid}|study:{guid}
/// PUT  api/v1/study-annotations   { key, annotations: [...] }  — replaces the study's set
/// </summary>
[ApiController]
[Authorize]
[RequiresModule(ModuleConstants.Pacs)]
[Route("api/v1/study-annotations")]
public class StudyAnnotationsController : ControllerBase
{
    private const int MaxJsonChars = 2_000_000;
    private const int MaxAnnotations = 2_000;

    private readonly IApplicationDbContext _db;
    private readonly IUserContext _user;

    public StudyAnnotationsController(IApplicationDbContext db, IUserContext user)
    {
        _db = db;
        _user = user;
    }

    // The study must exist at the caller's centre (global tenant filter applies).
    private async Task<string?> NormaliseKeyAsync(string? key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var parts = key.Trim().Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out var id)) return null;
        var kind = parts[0].ToLowerInvariant();
        var exists = kind switch
        {
            "appointment" => await _db.Appointments.AnyAsync(a => a.AppointmentId == id, ct),
            "study" => await _db.ImagingStudies.AnyAsync(s => s.Id == id, ct),
            _ => false,
        };
        return exists ? $"{kind}:{id:D}" : null;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? key, CancellationToken ct)
    {
        var k = await NormaliseKeyAsync(key, ct);
        if (k == null) return NotFound(new { success = false, error = "Study not found." });

        var row = await _db.StudyAnnotations.AsNoTracking().FirstOrDefaultAsync(a => a.StudyKey == k, ct);
        return Ok(new
        {
            success = true,
            data = new
            {
                key = k,
                annotations = row == null ? JsonDocument.Parse("[]").RootElement : JsonDocument.Parse(row.AnnotationsJson).RootElement,
                updatedAt = row?.UpdatedAt,
                updatedByUserId = row?.UpdatedByUserId,
            }
        });
    }

    public record SaveBody(string? Key, JsonElement Annotations);

    [HttpPut]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Doctor},{RoleConstants.Technician}")]
    public async Task<IActionResult> Save([FromBody] SaveBody body, CancellationToken ct)
    {
        var k = await NormaliseKeyAsync(body?.Key, ct);
        if (k == null) return NotFound(new { success = false, error = "Study not found." });
        if (body!.Annotations.ValueKind != JsonValueKind.Array)
            return BadRequest(new { success = false, error = "annotations must be an array." });

        var count = body.Annotations.GetArrayLength();
        var json = body.Annotations.GetRawText();
        if (count > MaxAnnotations || json.Length > MaxJsonChars)
            return BadRequest(new { success = false, error = "Too many annotations for one study." });

        var row = await _db.StudyAnnotations.FirstOrDefaultAsync(a => a.StudyKey == k, ct);
        if (row == null)
        {
            row = new StudyAnnotation { HospitalId = _user.HospitalId, StudyKey = k };
            _db.StudyAnnotations.Add(row);
        }
        row.AnnotationsJson = json;
        row.AnnotationCount = count;
        row.UpdatedByUserId = _user.UserId == Guid.Empty ? null : _user.UserId;
        row.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two viewers saved the first set for a study at the same instant; the
            // unique (HospitalId, StudyKey) index rejected one. Last write wins on retry.
            return Conflict(new { success = false, error = "Saved by someone else at the same moment — try again." });
        }
        return Ok(new { success = true, data = new { key = k, count, updatedAt = row.UpdatedAt } });
    }
}
