using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using _1Rad.Application.Features.Appointments;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
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
    IReadOnlyList<AppointmentServiceLine>? Services = null
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

        var count = await _context.Appointments.CountAsync(cancellationToken);

        // Compute a stable, persisted daily token number scoped to this hospital + date
        var appointmentDate = request.DateTime.Date;
        var hospitalId = _context.UserContext.HospitalId != Guid.Empty
            ? _context.UserContext.HospitalId
            : patient.HospitalId;

        var dailyTokenNumber = await _context.Appointments
            .CountAsync(a => a.HospitalId == hospitalId && a.DateTime.Date == appointmentDate, cancellationToken) + 1;

        // Resolve the service-lines list. v2 callers send Services directly;
        // v1 callers send only the scalar fields and we build a single-line
        // list from them so the new AppointmentService child rows are
        // populated regardless of which client shape arrived.
        var serviceLines = NormaliseServices(request);
        if (serviceLines.Count == 0)
        {
            throw new Exception("At least one service is required to book an appointment.");
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

        // The "primary" line (first in the list) is the denormalised snapshot
        // on the parent Appointment — kept for backward compat with v1
        // clients that still read Appointment.Service / .Modality directly.
        var primary = serviceLines[0];

        var appointment = new Appointment
        {
            DisplayId = $"APP-{101 + count}",
            PatientId = request.PatientId,
            PatientName = patient.FullName ?? "Unknown",
            Mobile = patient.Mobile,
            Service = primary.ServiceName,
            Modality = primary.Modality,
            DateTime = request.DateTime,
            Type = request.Type,
            Priority = NormalizePriority(request.Priority),
            Doctor = request.Doctor,
            Status = "scheduled",
            ReferredBy = request.ReferredBy,
            ReferredContact = request.ReferredContact,
            Notes = request.Notes,
            DailyTokenNumber = dailyTokenNumber,
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

        // Fetch Hospital settings to check for auto-billing preference
        var hospital = await _context.Hospitals.FindAsync(new object[] { appointment.HospitalId }, cancellationToken);
        bool isAutoBillingEnabled = hospital?.IsAutoBillingEnabled ?? false;

        // Aggregate totals across the whole visit — what hits the Invoice.
        decimal totalAmount        = serviceLines.Sum(l => l.Amount);
        decimal totalReferralCut   = serviceLines.Sum(l => l.ReferralCutValue);

        string? invoiceDisplayId = null;

        // Auto-bill: one Invoice for the whole visit, one InvoiceItem per
        // service line so the bill itemises each scan with its own price.
        if (totalAmount > 0 && isAutoBillingEnabled)
        {
            invoiceDisplayId = $"INV-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 8).ToUpper()}";
            var invoice = new Invoice
            {
                AppointmentId = appointment.AppointmentId,
                PatientId = request.PatientId,
                PatientName = patient.FullName ?? "Unknown",
                HospitalId = appointment.HospitalId,
                InvoiceId = invoiceDisplayId,
                GrossAmount = totalAmount,
                DiscountAmount = 0,
                TotalAmount = totalAmount,
                PaidAmount = 0,
                Status = "PENDING",
                ReferralCutValue = totalReferralCut,
                CreatedAt = DateTime.UtcNow
            };

            for (int i = 0; i < serviceLines.Count; i++)
            {
                var line = serviceLines[i];
                invoice.Items.Add(new InvoiceItem
                {
                    Description = line.ServiceName,
                    Amount = line.Amount,
                    Quantity = 1,
                    AppointmentServiceId = createdServices[i].Id
                });
            }

            _context.Invoices.Add(invoice);
        }

        // --- REFERRAL SYNCHRONISATION ---
        if (!string.IsNullOrEmpty(request.ReferredBy))
        {
            var searchName = request.ReferredBy.Trim();
            var referrer = await _context.Referrers
                .FirstOrDefaultAsync(r => r.Name.ToLower() == searchName.ToLower() && r.HospitalId == appointment.HospitalId, cancellationToken);

            if (referrer == null)
            {
                var digits = string.Empty;
                if (!string.IsNullOrEmpty(request.ReferredContact))
                {
                    digits = new string(request.ReferredContact.Where(char.IsDigit).ToArray());
                    if (digits.StartsWith("91") && digits.Length == 12)
                    {
                        digits = digits.Substring(2);
                    }
                    else if (digits.StartsWith("0") && digits.Length == 11)
                    {
                        digits = digits.Substring(1);
                    }
                }

                referrer = new Referrer
                {
                    Name = searchName,
                    Contact = digits,
                    Address = string.Empty,
                    HospitalId = appointment.HospitalId
                };

                _context.Referrers.Add(referrer);
            }

            // Ensure patient record is linked to this referrer for longitudinal tracking
            patient.ReferrerId = referrer.ReferrerId;

            // Generate one commission row per service line that carries a
            // positive cut (or unconditionally when auto-billing is on and
            // the line has a cut). Each commission carries the
            // AppointmentServiceId so the referrer dashboard can break
            // commissions down by modality / service.
            if (isAutoBillingEnabled || totalReferralCut > 0)
            {
                // The accumulated total walks forward across all lines so
                // the commission ledger remains a monotonic running sum.
                var currentTotal = await _context.ReferralCommissions
                    .Where(c => c.ReferrerId == referrer.ReferrerId && c.HospitalId == appointment.HospitalId)
                    .SumAsync(c => (decimal?)c.CommissionAmount, cancellationToken) ?? 0;

                for (int i = 0; i < serviceLines.Count; i++)
                {
                    var line = serviceLines[i];
                    if (line.ReferralCutValue <= 0 && !isAutoBillingEnabled) continue;

                    currentTotal += line.ReferralCutValue;

                    var commission = new ReferralCommission
                    {
                        ReferrerId = referrer.ReferrerId,
                        ReferrerName = referrer.Name ?? request.ReferredBy ?? "Self-Referral",
                        Modality = line.Modality,
                        CommissionAmount = line.ReferralCutValue,
                        AccumulatedTotal = currentTotal,
                        Status = "UNPAID",
                        TransactionDate = DateTime.UtcNow,
                        HospitalId = appointment.HospitalId,
                        AppointmentId = appointment.AppointmentId,
                        AppointmentServiceId = createdServices[i].Id,
                        ReferenceNumber = invoiceDisplayId
                    };

                    _context.ReferralCommissions.Add(commission);
                }
            }
        }


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
                    ReferralCutValue: l.ReferralCutValue,
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
                ReferralCutValue: request.ReferralCutValue ?? 0,
                Id:               null,
                ServiceChargeId:  null)
        };
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
