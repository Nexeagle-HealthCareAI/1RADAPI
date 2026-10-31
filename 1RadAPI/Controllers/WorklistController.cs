using System.Globalization;
using System.Text.Json.Nodes;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Constants;
using _1Rad.Infrastructure.Interop;
using _1RadAPI.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace _1RadAPI.Controllers;

/// <summary>
/// Modality worklist feed — gap analysis P3-6, phase 3.
///
/// GET api/v1/worklist/mwl?date=YYYYMMDD&amp;modality=CT
///   → DICOM JSON array, one item per booked service that is not yet scanned.
///
/// The modality itself speaks DIMSE C-FIND, which stays on-prem: the bridge polls
/// this endpoint (integration key with scope worklist.read) and writes Orthanc
/// worklist files. Each item carries a Study Instance UID made from the service id;
/// when the images come back with it, study matching links them to that exact
/// service — no re-typed names, no "unmatched study" inbox.
/// </summary>
[ApiController]
[Authorize]
[RequiresModule(ModuleConstants.Ris)]
[Route("api/v1/worklist")]
public class WorklistController : ControllerBase
{
    private static readonly string[] OpenAppointmentStatuses = { "BOOKED", "CONFIRMED", "ARRIVED", "IN_PROGRESS" };
    private static readonly string[] OpenServiceStatuses = { "NOT_STARTED", "IN_PROGRESS", "IN_MID" };

    private readonly IApplicationDbContext _db;

    public WorklistController(IApplicationDbContext db) => _db = db;

    [HttpGet("mwl")]
    [IntegrationScope(IntegrationScopes.WorklistRead)]
    public async Task<IActionResult> Get([FromQuery] string? date, [FromQuery] string? modality, [FromQuery] int days = 1, CancellationToken ct = default)
    {
        // The IST calendar day(s) asked for (default: today in India).
        DateTime day;
        if (string.IsNullOrWhiteSpace(date))
            day = (DateTime.UtcNow + WorklistMapper.Ist).Date;
        else if (!DateTime.TryParseExact(date.Replace("-", ""), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
            return BadRequest(new { error = "date must be YYYYMMDD." });
        days = Math.Clamp(days, 1, 7);
        // Appointment.DateTime is India clock time, so the IST days compare directly.
        var fromUtc = IstDateRange.ClockStart(day);
        var toUtc = IstDateRange.ClockStart(day.AddDays(days));

        var rows = await _db.AppointmentServices.AsNoTracking()
            .Where(s => s.DeletedAt == null
                        && s.CancelledAt == null
                        && s.ScanCompletedAt == null
                        && (s.Status == null || OpenServiceStatuses.Contains(s.Status))
                        && s.Appointment.DeletedAt == null
                        && s.Appointment.DateTime >= fromUtc
                        && s.Appointment.DateTime < toUtc
                        && (s.Appointment.Status == null || OpenAppointmentStatuses.Contains(s.Appointment.Status)))
            .OrderBy(s => s.Appointment.DateTime)
            .ThenBy(s => s.AppointmentId)
            .Select(s => new
            {
                s.Id,
                s.AppointmentId,
                s.Appointment.DisplayId,
                s.Appointment.DateTime,
                s.Appointment.Priority,
                AppointmentStatus = s.Appointment.Status,
                s.Appointment.ReferredBy,
                s.ServiceName,
                ServiceModality = s.Modality,
                AppointmentModality = s.Appointment.Modality,
                s.Appointment.PatientId,
                PatientName = s.Appointment.Patient.FullName ?? s.Appointment.PatientName,
                s.Appointment.Patient.PatientIdentifier,
                s.Appointment.Patient.Gender,
                s.Appointment.Patient.Age,
            })
            .Take(2000)
            .ToListAsync(ct);

        var wantedModality = string.IsNullOrWhiteSpace(modality) ? null : modality.Trim().ToUpperInvariant();
        var result = new JsonArray();
        foreach (var appt in rows.GroupBy(r => r.AppointmentId))
        {
            var index = 0;
            foreach (var r in appt)
            {
                var entry = new WorklistEntry(
                    r.AppointmentId, r.Id, r.DisplayId, r.DateTime, r.Priority, r.AppointmentStatus, r.ReferredBy,
                    r.ServiceName, r.ServiceModality, r.AppointmentModality, r.PatientId, r.PatientName,
                    r.PatientIdentifier, r.Gender, r.Age);
                var item = WorklistMapper.ToItem(entry, index++);
                if (wantedModality != null)
                {
                    var code = WorklistMapper.DicomModality(r.ServiceModality) ?? WorklistMapper.DicomModality(r.AppointmentModality) ?? "OT";
                    if (code != wantedModality) continue;
                }
                result.Add(item);
            }
        }
        return Content(result.ToJsonString(), "application/dicom+json");
    }
}
