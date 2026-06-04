using Microsoft.EntityFrameworkCore;
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
        CancellationToken ct)
    {
        var name = (newReferrerName ?? string.Empty).Trim();
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

        // Update the appointment's referral fields.
        appointment.ReferredBy = name;
        if (!string.IsNullOrEmpty(contact)) appointment.ReferredContact = contact;
        appointment.UpdatedAt = DateTime.UtcNow;

        // Re-point this appointment's commission rows; remember who they leave.
        var commissions = await ctx.ReferralCommissions
            .Where(c => c.AppointmentId == appointmentId && c.HospitalId == hospitalId && c.DeletedAt == null)
            .ToListAsync(ct);

        var affected = new HashSet<Guid>();
        foreach (var c in commissions)
        {
            if (c.ReferrerId != Guid.Empty) affected.Add(c.ReferrerId);
            c.ReferrerId = referrer.ReferrerId;
            c.ReferrerName = referrer.Name ?? name;
            // Re-pointing to Self zeroes the cut (no commission for a walk-in).
            if (isSelf) c.CommissionAmount = 0;
            c.UpdatedAt = DateTime.UtcNow;
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
