using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using _1Rad.Application.Common;
using _1Rad.Application.Features.Appointments;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Commands.CreateAppointment;

/// <summary>
/// Book an appointment.
///
/// Two payload shapes are accepted:
///   v1 (legacy) — scalar Service + Modality + Amount + ReferralCutValue.
///                 Older PWA installs and any caller that hasn't migrated
///                 yet. Handler synthesises a single AppointmentService
///                 line from the scalars so the new child rows are still
///                 written.
///   v2 (multi)  — Services list with one line per scan to do (X-ray + CT
///                 + USG). Scalar fields are derived from the first line
///                 in the list as a backward-compat "primary service"
///                 denormalisation on the Appointment row.
/// </summary>
public record CreateAppointmentCommand(
    Guid PatientId,
    string Service,
    string Modality,
    DateTime DateTime,
    string Type,
    string Doctor,
    string ReferredBy,
    string ReferredContact,
    string Notes,
    decimal Amount = 0,
    decimal? ReferralCutValue = null,
    // Clinical urgency: STAT / URGENT / ROUTINE. Drives worklist sort order.
    // Front desk picks this when booking; default ROUTINE.
    string Priority = "ROUTINE",
    // Multi-service support (step 2). When supplied this becomes the source
    // of truth; the scalar Service/Modality/Amount/ReferralCutValue above
    // are ignored as input (they are re-derived from the first line as a
    // denormalised "primary service" snapshot on the parent Appointment).
    IReadOnlyList<AppointmentServiceLine>? Services = null,
    // Optional referring-doctor profile — stored on the Referrer the first
    // time we see this doctor (and refreshed if a later booking sends them).
    string? ReferrerEmail = null,
    string? ReferrerSpecialty = null,
    string? ReferrerDegree = null,
    // Payee-first model. The referrer IS the payee. IsDoctor distinguishes a
    // self-paying doctor (uses the profile fields) from another person/agent
    // (SupportedByDoctor names the doctor they collect on behalf of).
    bool ReferrerIsDoctor = true,
    string? ReferrerSupportedByDoctor = null,
    // When the payee is an agent, the supporting doctor's profile so that
    // doctor is upserted into the partner list (reusable + auto-selectable).
    string? ReferrerSupportedSpecialty = null,
    string? ReferrerSupportedDegree = null,
    // Legacy per-booking payee fields (superseded by the payee-first model;
    // kept so older clients still bind without error).
    string? ReferralPayeeName = null,
    string? ReferralPayeeContact = null
) : IRequest<Guid>;


public class CreateAppointmentCommandHandler : IRequestHandler<CreateAppointmentCommand, Guid>
{
    private readonly IApplicationDbContext _context;

    public CreateAppointmentCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Guid> Handle(CreateAppointmentCommand request, CancellationToken cancellationToken)
    {
        var patient = await _context.Patients
            .FirstOrDefaultAsync(p => p.PatientId == request.PatientId, cancellationToken);

        if (patient == null) throw new Exception("Patient not found.");

        var hospitalId = _context.UserContext.HospitalId != Guid.Empty
            ? _context.UserContext.HospitalId
            : patient.HospitalId;

        // --- CONCURRENCY-SAFE DISPLAY ID ---
        // The display id ("APP-###") used to be "count of rows + 1" — a
        // read-then-write that let several terminals booking at the same instant
        // compute the SAME number. Hand it out atomically instead
        // (NextSequenceValueAsync increments-and-returns under a key-range lock).
        //
        // NOTE: the daily TOKEN number is intentionally NOT assigned here. Tokens
        // are now generated when the patient ARRIVES (the "Arrived" status), so
        // the number reflects real arrival order rather than booking order. See
        // UpdateAppointmentStatusCommand.
        var existingTotal = await _context.Appointments.CountAsync(cancellationToken);
        var displaySeq = await _context.NextSequenceValueAsync(
            Guid.Empty, "APPOINTMENT_DISPLAY_ID", 101 + existingTotal, cancellationToken);

        // Resolve the service-lines list. v2 callers send Services directly;
        // v1 callers send only the scalar fields and we build a single-line
        // list from them so the new AppointmentService child rows are
        // populated regardless of which client shape arrived.
        var serviceLines = NormaliseServices(request);
        if (serviceLines.Count == 0)
        {
            throw new Exception("At least one service is required to book an appointment.");
        }

        // ── DUPLICATE-APPOINTMENT GUARD ──────────────────────────────────────
        // Block an accidental re-entry: the SAME patient identity (name + age +
        // gender) already has a non-cancelled appointment TODAY for one of the
        // SAME services. Surface the existing appointment's time, token and
        // patient id so the front desk can verify instead of double-booking.
        var dupDate = request.DateTime.Date;
        var dupNameLower = (patient.FullName ?? string.Empty).Trim().ToLower();
        var dupCandidates = await _context.Appointments
            .Where(a => a.HospitalId == hospitalId
                     && a.DateTime.Date == dupDate
                     && a.Status != "CANCELLED"
                     && a.Patient.FullName != null
                     && a.Patient.FullName.ToLower() == dupNameLower
                     && a.Patient.Age == patient.Age
                     && a.Patient.Gender == patient.Gender)
            .Select(a => new
            {
                a.AppointmentId,
                a.DisplayId,
                a.DailyTokenNumber,
                a.DateTime,
                PatientIdentifier = a.Patient.PatientIdentifier
            })
            .ToListAsync(cancellationToken);

        if (dupCandidates.Count > 0)
        {
            var candidateIds = dupCandidates.Select(a => a.AppointmentId).ToList();
            var existingLines = await _context.AppointmentServices
                .Where(s => candidateIds.Contains(s.AppointmentId) && s.DeletedAt == null && s.Status != "CANCELLED")
                .Select(s => new { s.AppointmentId, s.Modality, s.ServiceName })
                .ToListAsync(cancellationToken);

            static string Norm(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();
            var newSet = serviceLines
                .Select(l => (Mod: Norm(l.Modality), Svc: Norm(l.ServiceName)))
                .ToHashSet();

            var clash = dupCandidates.FirstOrDefault(appt =>
                existingLines.Any(el => el.AppointmentId == appt.AppointmentId
                                     && newSet.Contains((Norm(el.Modality), Norm(el.ServiceName)))));

            if (clash != null)
            {
                var timeStr  = clash.DateTime.ToString("hh:mm tt");
                var tokenStr = clash.DailyTokenNumber.HasValue ? $" Token No: {clash.DailyTokenNumber.Value}." : string.Empty;
                var pidStr   = string.IsNullOrWhiteSpace(clash.PatientIdentifier) ? string.Empty : $" Patient ID: {clash.PatientIdentifier}.";
                throw new ConflictException(
                    $"This patient already has an appointment today (booked at {timeStr}) for the same service.{tokenStr}{pidStr} Reference: {clash.DisplayId}. Please verify before booking again.");
            }
        }

        // Safeguard: the Lead Specialist must not be a non-doctor staffer (e.g. a
        // custom-role or admin/technician user mistakenly assigned). We match the
        // assigned name to a user ON THIS CENTRE'S roster; if that user exists and
        // holds roles but NONE is a doctor role, reject. Names that match no
        // on-roster user (an externally-typed referring physician) are allowed.
        // Best-effort — a lookup hiccup must never block a legitimate booking.
        if (!string.IsNullOrWhiteSpace(request.Doctor))
        {
            var docName = request.Doctor.Trim().ToLower();
            bool assignedIsNonDoctor = false;
            try
            {
                var match = await _context.Users
                    .Where(u => u.FullName != null
                                && u.FullName.ToLower() == docName
                                && u.HospitalMappings.Any(m => m.HospitalId == hospitalId))
                    .Select(u => new
                    {
                        IsDoctor = u.HospitalMappings
                            .Where(m => m.HospitalId == hospitalId)
                            .SelectMany(m => m.Roles)
                            .Any(r => r.RoleName != null && r.RoleName.ToLower().Contains("doctor")),
                        HasAnyRole =
                            u.HospitalMappings.Where(m => m.HospitalId == hospitalId).SelectMany(m => m.Roles).Any()
                            || u.HospitalMappings.Where(m => m.HospitalId == hospitalId).SelectMany(m => m.CustomRoles).Any()
                    })
                    .FirstOrDefaultAsync(cancellationToken);

                if (match != null && match.HasAnyRole && !match.IsDoctor)
                    assignedIsNonDoctor = true;
            }
            catch { /* best-effort — never block a booking on a roster lookup error */ }

            if (assignedIsNonDoctor)
                throw new Exception("The assigned Lead Specialist is not a registered doctor at this centre. Please assign a doctor to supervise this appointment.");
        }

        // --- REFERRER RESOLUTION (race-safe, before the appointment is built) ---
        // Resolve or create the referrer up front and commit a brand-new one on
        // its own. This closes the race where two bookings naming the SAME new
        // referrer at once each insert a row (splitting that doctor's
        // commissions). The unique index UX_Referrers_Hospital_Name makes the
        // losing insert fail; we catch it, re-read the winner, and carry on with
        // the existing referrer — no duplicate, no failed booking.
        Referrer? referrer = null;
        if (!string.IsNullOrEmpty(request.ReferredBy))
        {
            // Canonical stored form + honorific-insensitive match key (#13/#15):
            // "aquib", "Aquib" and "Md Aquib" all resolve to the SAME referrer
            // instead of spawning duplicates, and the name is stored UPPERCASE.
            var searchName = NameNormalizer.Upper(request.ReferredBy);
            var nameKey = NameNormalizer.Normalize(request.ReferredBy);
            referrer = await _context.Referrers
                .FirstOrDefaultAsync(r => r.Name == searchName && r.HospitalId == hospitalId, cancellationToken);

            if (referrer == null)
            {
                // Honorific-insensitive fallback so "AQUIB" reuses "MD AQUIB".
                var hospRefs = await _context.Referrers
                    .Where(r => r.HospitalId == hospitalId)
                    .ToListAsync(cancellationToken);
                referrer = hospRefs.FirstOrDefault(r => NameNormalizer.Normalize(r.Name) == nameKey);
            }

            if (referrer == null)
            {
                var digits = string.Empty;
                if (!string.IsNullOrEmpty(request.ReferredContact))
                {
                    digits = new string(request.ReferredContact.Where(char.IsDigit).ToArray());
                    if (digits.StartsWith("91") && digits.Length == 12)
                        digits = digits.Substring(2);
                    else if (digits.StartsWith("0") && digits.Length == 11)
                        digits = digits.Substring(1);
                }

                referrer = new Referrer
                {
                    Name = searchName,
                    Contact = digits,
                    Address = string.Empty,
                    HospitalId = hospitalId,
                    IsDoctor  = request.ReferrerIsDoctor,
                    SupportedByDoctor = request.ReferrerIsDoctor ? null : NameNormalizer.UpperOrNull(request.ReferrerSupportedByDoctor),
                    Email     = NullIfBlank(request.ReferrerEmail),
                    Specialty = NullIfBlank(request.ReferrerSpecialty),
                    Degree    = NullIfBlank(request.ReferrerDegree),
                };
                _context.Referrers.Add(referrer);

                try
                {
                    await _context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    // Lost the create race — another booking inserted this
                    // referrer first. Drop our duplicate and use the winner.
                    _context.Entry(referrer).State = EntityState.Detached;
                    referrer = await _context.Referrers
                        .FirstOrDefaultAsync(r => r.Name!.ToLower() == searchName.ToLower() && r.HospitalId == hospitalId, cancellationToken);
                    if (referrer == null) throw; // couldn't resolve at all — surface it
                }
            }

            // Keep the referral record fresh — fill in anything this booking
            // supplied (only overwrite when a non-blank value arrived, so an
            // empty booking field never wipes a previously-saved profile).
            referrer.IsDoctor  = request.ReferrerIsDoctor;
            referrer.SupportedByDoctor = request.ReferrerIsDoctor
                ? null
                : (NameNormalizer.UpperOrNull(request.ReferrerSupportedByDoctor) ?? referrer.SupportedByDoctor);
            referrer.Email     = NullIfBlank(request.ReferrerEmail)     ?? referrer.Email;
            referrer.Specialty = NullIfBlank(request.ReferrerSpecialty) ?? referrer.Specialty;
            referrer.Degree    = NullIfBlank(request.ReferrerDegree)    ?? referrer.Degree;

            // Link the patient to this referrer for longitudinal tracking.
            patient.ReferrerId = referrer.ReferrerId;

            // When the payee is an agent, make sure the supporting doctor also
            // exists as a partner doctor — so their profile (speciality/degree)
            // is stored once and they become auto-selectable on the next booking.
            if (!request.ReferrerIsDoctor)
            {
                var supportName = NameNormalizer.UpperOrNull(request.ReferrerSupportedByDoctor);
                if (supportName != null)
                {
                    var supportDoc = await _context.Referrers
                        .FirstOrDefaultAsync(r => r.Name!.ToLower() == supportName.ToLower() && r.HospitalId == hospitalId, cancellationToken);

                    if (supportDoc == null)
                    {
                        supportDoc = new Referrer
                        {
                            Name = supportName,
                            Contact = string.Empty,
                            Address = string.Empty,
                            HospitalId = hospitalId,
                            IsDoctor = true,
                            Specialty = NullIfBlank(request.ReferrerSupportedSpecialty),
                            Degree    = NullIfBlank(request.ReferrerSupportedDegree),
                        };
                        _context.Referrers.Add(supportDoc);
                        try
                        {
                            await _context.SaveChangesAsync(cancellationToken);
                        }
                        catch (DbUpdateException)
                        {
                            _context.Entry(supportDoc).State = EntityState.Detached;
                            supportDoc = await _context.Referrers
                                .FirstOrDefaultAsync(r => r.Name!.ToLower() == supportName.ToLower() && r.HospitalId == hospitalId, cancellationToken);
                        }
                    }

                    // Refresh the doctor's profile only when this booking
                    // actually supplied a value (never wipe a saved profile).
                    if (supportDoc != null)
                    {
                        supportDoc.Specialty = NullIfBlank(request.ReferrerSupportedSpecialty) ?? supportDoc.Specialty;
                        supportDoc.Degree    = NullIfBlank(request.ReferrerSupportedDegree)    ?? supportDoc.Degree;
                    }
                }
            }
        }

        // The "primary" line (first in the list) is the denormalised snapshot
        // on the parent Appointment — kept for backward compat with v1
        // clients that still read Appointment.Service / .Modality directly.
        var primary = serviceLines[0];

        var appointment = new Appointment
        {
            DisplayId = $"APP-{displaySeq}",
            PatientId = request.PatientId,
            PatientName = NameNormalizer.UpperOrNull(patient.FullName) ?? "UNKNOWN",
            Mobile = patient.Mobile,
            Service = primary.ServiceName,
            Modality = primary.Modality,
            DateTime = request.DateTime,
            Type = request.Type,
            Priority = NormalizePriority(request.Priority),
            Doctor = request.Doctor,
            Status = "scheduled",
            ReferredBy = referrer?.Name ?? NameNormalizer.Upper(request.ReferredBy),
            ReferredContact = request.ReferredContact,
            // Per-appointment supporting doctor — only for an agent referral.
            SupportedByDoctor = request.ReferrerIsDoctor ? null : NameNormalizer.UpperOrNull(request.ReferrerSupportedByDoctor),
            Notes = request.Notes,
            // Token assigned on arrival, not at booking (see status handler).
            DailyTokenNumber = null,
            HospitalId = hospitalId
        };

        _context.Appointments.Add(appointment);

        // Spawn one AppointmentService row per line. Each carries its own
        // status, TAT timestamps, amount and referral cut.
        var createdServices = new List<AppointmentService>(serviceLines.Count);
        foreach (var line in serviceLines)
        {
            var svc = new AppointmentService
            {
                AppointmentId = appointment.AppointmentId,
                ServiceChargeId = line.ServiceChargeId,
                ServiceName = line.ServiceName,
                Modality = line.Modality,
                Amount = line.Amount,
                ReferralCutValue = line.ReferralCutValue,
                Status = "NOT_STARTED",
                HospitalId = hospitalId
            };
            _context.AppointmentServices.Add(svc);
            createdServices.Add(svc);
        }

        // NOTE: Billing (Invoice) and referral commissions are NO LONGER created
        // at booking. They are generated when the patient ARRIVES (first time the
        // appointment is marked CONFIRMED) in UpdateAppointmentStatusCommand — so
        // a no-show never produces a bill or a referral payout, and the financial
        // records mirror who actually showed up. Booking persists only the
        // appointment, its service lines, and the resolved referrer link.

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            var innerMessage = ex.InnerException?.Message ?? "No inner exception details";
            throw new Exception($"MISSION PERSISTENCE FAILURE: {ex.Message}. Database says: {innerMessage}", ex);
        }
        catch (Exception ex)
        {
            throw new Exception($"UNEXPECTED SYSTEM ERROR: {ex.Message}", ex);
        }

        return appointment.AppointmentId;
    }

    // Resolve the canonical list of service lines from the incoming command.
    // v2 callers populate Services directly; v1 callers send only the
    // scalar Service/Modality/Amount/ReferralCutValue and we synthesise a
    // one-element list from those so the rest of the handler doesn't care
    // which shape arrived.
    private static List<AppointmentServiceLine> NormaliseServices(CreateAppointmentCommand request)
    {
        if (request.Services is { Count: > 0 })
        {
            return request.Services
                .Where(l => !string.IsNullOrWhiteSpace(l.ServiceName) || !string.IsNullOrWhiteSpace(l.Modality))
                .Select(l => new AppointmentServiceLine(
                    ServiceName:      (l.ServiceName ?? string.Empty).Trim(),
                    Modality:         (l.Modality ?? string.Empty).Trim().ToUpperInvariant(),
                    Amount:           l.Amount,
                    // Floor the referral cut at zero. A modality with no incentive
                    // must produce a 0 commission, never a negative one (which
                    // would show up as a negative payout to the referrer).
                    ReferralCutValue: Math.Max(0m, l.ReferralCutValue),
                    Id:               null,
                    ServiceChargeId:  l.ServiceChargeId))
                .ToList();
        }

        // v1 fall-through. Synthesise a single line from the scalars.
        return new List<AppointmentServiceLine>
        {
            new AppointmentServiceLine(
                ServiceName:      (request.Service ?? string.Empty).Trim(),
                Modality:         (request.Modality ?? string.Empty).Trim().ToUpperInvariant(),
                Amount:           request.Amount,
                ReferralCutValue: Math.Max(0m, request.ReferralCutValue ?? 0),
                Id:               null,
                ServiceChargeId:  null)
        };
    }

    // Trim a string; return null when it's null/empty/whitespace so optional
    // profile + payee fields never store an empty string.
    private static string? NullIfBlank(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Keep only the digits of a contact number (light tidy of a payee phone).
    private static string? SanitizeContact(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("91") && digits.Length == 12) digits = digits.Substring(2);
        else if (digits.StartsWith("0") && digits.Length == 11) digits = digits.Substring(1);
        return string.IsNullOrEmpty(digits) ? null : digits;
    }

    // Whitelist + normalise — guards the DB from arbitrary strings reaching
    // the Priority column. Unknown / null / blank values fall back to ROUTINE.
    private static string NormalizePriority(string? raw)
    {
        var v = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return v switch
        {
            "STAT"    => "STAT",
            "URGENT"  => "URGENT",
            "ROUTINE" => "ROUTINE",
            _         => "ROUTINE",
        };
    }
}
