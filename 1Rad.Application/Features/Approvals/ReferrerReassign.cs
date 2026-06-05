using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;

namespace _1Rad.Application.Features.Approvals;

/// <summary>
/// Re-points an appointment's referrer so the referral commission credits the
/// correct person. Updates the appointment's ReferredBy/contact, resolves (or
/// creates) the target referrer, moves this appointment's commission rows onto
/// that referrer, links the patient, and re-bases the accumulated totals of
/// every affected referrer (old + new) over their live commissions.
///
/// Shared by the direct path (ChangeReferrerCommand, when nothing is paid yet)
/// and the approved path (ReviewApproval CHANGE_REFERRER, when payment had
/// already been collected). An intermediate save makes the commission moves
/// visible to the accumulated-total re-base query; the caller persists the
/// re-base with its own final SaveChanges.
/// </summary>
internal static class ReferrerReassign
{
    public static async Task ApplyAsync(
        IApplicationDbContext ctx,
        Guid appointmentId,
        Guid hospitalId,
        string newReferrerName,
        string? newReferrerContact,
        bool? newReferrerIsDoctor,
        CancellationToken ct,
        bool preserveHistory = false,
        string? supportedByDoctor = null,
        string? email = null,
        string? specialty = null,
        string? degree = null,
        string? address = null,
        string? supportedSpecialty = null,
        string? supportedDegree = null)
    {
        // Stored UPPERCASE for consistency (#15); blank guards out.
        var name = NameNormalizer.Upper(newReferrerName);
        if (string.IsNullOrEmpty(name)) return;

        // "Self" / walk-in pays NO commission — the centre keeps the whole fee.
        var isSelf = string.Equals(name, "Self", StringComparison.OrdinalIgnoreCase);

        var appointment = await ctx.Appointments
            .FirstOrDefaultAsync(a => a.AppointmentId == appointmentId && a.HospitalId == hospitalId, ct);
        if (appointment == null) return;

        // Resolve / create the target referrer (hospital-scoped, case-insensitive).
        var referrer = await ctx.Referrers
            .FirstOrDefaultAsync(r => r.Name!.ToLower() == name.ToLower() && r.HospitalId == hospitalId, ct);
        if (referrer == null)
        {
            referrer = new Referrer
            {
                Name = name,
                Contact = string.Empty,
                Address = string.Empty,
                HospitalId = hospitalId,
                IsDoctor = newReferrerIsDoctor ?? true,
            };
            ctx.Referrers.Add(referrer);
        }
        if (newReferrerIsDoctor.HasValue) referrer.IsDoctor = newReferrerIsDoctor.Value;

        var contact = (newReferrerContact ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(contact))
        {
            var digits = new string(contact.Where(char.IsDigit).ToArray());
            if (digits.StartsWith("91") && digits.Length == 12) digits = digits.Substring(2);
            else if (digits.StartsWith("0") && digits.Length == 11) digits = digits.Substring(1);
            if (digits.Length > 0) referrer.Contact = digits;
        }

        // Full profile (parity with booking) — only overwrite when a value is given.
        if (!string.IsNullOrWhiteSpace(email)) referrer.Email = email.Trim();
        if (!string.IsNullOrWhiteSpace(address)) referrer.Address = address.Trim();
        if ((newReferrerIsDoctor ?? referrer.IsDoctor))
        {
            if (!string.IsNullOrWhiteSpace(specialty)) referrer.Specialty = specialty.Trim();
            if (!string.IsNullOrWhiteSpace(degree)) referrer.Degree = degree.Trim();
        }

        // Update the appointment's referral fields.
        appointment.ReferredBy = name;
        if (!string.IsNullOrEmpty(contact)) appointment.ReferredContact = contact;

        // "Other person" (agent) → record the doctor they collect for. Doctors and
        // Self carry no supporting doctor.
        var supDoc = NameNormalizer.Upper(supportedByDoctor);
        if (!isSelf && newReferrerIsDoctor == false && supDoc.Length > 0)
        {
            appointment.SupportedByDoctor = supDoc;
            referrer.SupportedByDoctor = supDoc;

            // Upsert the supporting doctor's own referrer record so their profile
            // (speciality / degree) is stored, mirroring booking.
            var supRef = await ctx.Referrers
                .FirstOrDefaultAsync(r => r.Name!.ToLower() == supDoc.ToLower() && r.HospitalId == hospitalId, ct);
            if (supRef == null)
            {
                supRef = new Referrer { Name = supDoc, Contact = string.Empty, Address = string.Empty, HospitalId = hospitalId, IsDoctor = true };
                ctx.Referrers.Add(supRef);
            }
            supRef.IsDoctor = true;
            if (!string.IsNullOrWhiteSpace(supportedSpecialty)) supRef.Specialty = supportedSpecialty.Trim();
            if (!string.IsNullOrWhiteSpace(supportedDegree)) supRef.Degree = supportedDegree.Trim();
        }
        else
        {
            appointment.SupportedByDoctor = null;
        }
        appointment.UpdatedAt = DateTime.UtcNow;

        // Re-point this appointment's commission rows; remember who they leave.
        var commissions = await ctx.ReferralCommissions
            .Where(c => c.AppointmentId == appointmentId && c.HospitalId == hospitalId && c.DeletedAt == null)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var affected = new HashSet<Guid>();
        foreach (var c in commissions)
        {
            var oldReferrerId = c.ReferrerId;
            if (oldReferrerId != Guid.Empty) affected.Add(oldReferrerId);

            // Paid path (preserveHistory) → true double-entry: the old referrer's
            // original row is KEPT, a reversal (−amount) is booked against them, and
            // a fresh entry is credited to the new referrer. History is never lost,
            // and a clawback shows if the old cut was already paid. The simple move
            // (unpaid edit, Self, or a zero cut) just re-points the row.
            if (preserveHistory && oldReferrerId != referrer.ReferrerId && c.CommissionAmount != 0)
            {
                var amount = c.CommissionAmount;

                // Reversal against the OLD referrer (cancels the original).
                ctx.ReferralCommissions.Add(new ReferralCommission
                {
                    HospitalId = hospitalId,
                    ReferrerId = oldReferrerId,
                    ReferrerName = c.ReferrerName,
                    Modality = c.Modality,
                    PatientName = c.PatientName,
                    AppointmentId = c.AppointmentId,
                    AppointmentServiceId = c.AppointmentServiceId,
                    ReferenceNumber = c.ReferenceNumber,
                    CommissionAmount = -amount,
                    Status = "UNPAID",
                    TransactionDate = now,
                    ServiceDate = c.ServiceDate,
                    Remarks = $"[Reversal — referrer changed to {referrer.Name}; was ₹{amount:0.##} credited to {c.ReferrerName}]",
                });

                // Fresh credit to the NEW referrer (Self earns nothing).
                ctx.ReferralCommissions.Add(new ReferralCommission
                {
                    HospitalId = hospitalId,
                    ReferrerId = referrer.ReferrerId,
                    ReferrerName = referrer.Name ?? name,
                    Modality = c.Modality,
                    PatientName = c.PatientName,
                    AppointmentId = c.AppointmentId,
                    AppointmentServiceId = c.AppointmentServiceId,
                    ReferenceNumber = c.ReferenceNumber,
                    CommissionAmount = isSelf ? 0 : amount,
                    Status = "UNPAID",
                    TransactionDate = now,
                    ServiceDate = c.ServiceDate,
                    Remarks = $"[Reassigned from {c.ReferrerName} via approval]",
                });

                // Keep the original as immutable history.
                c.Remarks = (c.Remarks ?? "") + $" [Referrer changed to {referrer.Name} — reversed by ledger entry]";
                c.UpdatedAt = now;
            }
            else
            {
                // Move: re-point the existing row to the new referrer.
                c.ReferrerId = referrer.ReferrerId;
                c.ReferrerName = referrer.Name ?? name;
                if (isSelf) c.CommissionAmount = 0; // Self / walk-in earns no cut.
                c.UpdatedAt = now;
            }
        }
        affected.Add(referrer.ReferrerId);

        // Keep the patient's link pointing at the corrected referrer.
        var patient = await ctx.Patients
            .FirstOrDefaultAsync(p => p.PatientId == appointment.PatientId, ct);
        if (patient != null) patient.ReferrerId = referrer.ReferrerId;

        // Persist the moves so the re-base query below sees current ownership.
        await ctx.SaveChangesAsync(ct);

        // Re-base accumulated totals for every affected referrer over live rows.
        foreach (var rid in affected)
        {
            var rows = await ctx.ReferralCommissions
                .Where(c => c.ReferrerId == rid && c.HospitalId == hospitalId && c.DeletedAt == null)
                .OrderBy(c => c.TransactionDate)
                .ToListAsync(ct);
            decimal running = 0;
            foreach (var c in rows) { running += c.CommissionAmount; c.AccumulatedTotal = running; }
        }
    }
}
