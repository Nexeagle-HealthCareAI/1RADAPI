using _1Rad.Application.Common;
using _1Rad.Domain.Exceptions;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.PayReferralCommissions;

/// <summary>
/// Settles a set of commissions in one payout — ONE transaction, ONE set of
/// disbursement details. The Referral Hub used to fire one PATCH per commission
/// from the browser: a dropped connection or a single rejected row left the
/// payout half-recorded (some rows PAID, some not, all stamped as one payment),
/// and a retry then failed on the rows that had already gone through.
///
/// Semantics:
///   • All-or-nothing on the rows it accepts (single SaveChanges).
///   • Idempotent: a row that is already PAID is reported as skipped, never an
///     error, so re-submitting the same payout is safe.
///   • Rows it will not pay (cancelled, non-positive, patient hasn't paid yet) are
///     reported back with the reason instead of failing the whole payout.
///   • NetDeficits (opt-in): the partner's outstanding clawback/reversal deficit
///     (negative UNPAID rows) is recovered out of this payout, oldest first, so the
///     cash actually handed over is gross − recovered. A deficit row that can only be
///     partly recovered is split: the recovered part is settled, the rest stays
///     outstanding.
/// </summary>
public record PayReferralCommissionsCommand(
    List<Guid> CommissionIds,
    string PaidBy,
    string PayeeName,
    string? PayeeContact = null,
    string? PayeeEmail = null,
    string? PayeeAddress = null,
    bool NetDeficits = false
) : IRequest<PayReferralCommissionsResult>;

public record SkippedCommission(Guid CommissionId, string Reason);

/// <param name="TotalPaid">Gross of the commissions marked paid.</param>
/// <param name="DeficitRecovered">Deficit recovered out of this payout (0 unless NetDeficits).</param>
/// <param name="NetPaid">Cash to hand over: TotalPaid − DeficitRecovered.</param>
public record PayReferralCommissionsResult(
    List<Guid> Paid,
    List<SkippedCommission> Skipped,
    decimal TotalPaid,
    decimal DeficitRecovered = 0m,
    decimal NetPaid = 0m);

public class PayReferralCommissionsCommandHandler : IRequestHandler<PayReferralCommissionsCommand, PayReferralCommissionsResult>
{
    private readonly IApplicationDbContext _context;

    public PayReferralCommissionsCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<PayReferralCommissionsResult> Handle(PayReferralCommissionsCommand request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty)
            throw new UnauthorizedAccessException("Hospital context required.");

        var paidBy = (request.PaidBy ?? string.Empty).Trim();
        var payeeName = (request.PayeeName ?? string.Empty).Trim();
        if (paidBy.Length == 0)
            throw new ValidationException("Paid By is required to record a payout.");
        if (payeeName.Length == 0)
            throw new ValidationException("Paid To (name) is required to record a payout.");

        // Free text from the payout form, into fixed-width columns: refuse it with a message rather than
        // let the database reject it as a masked error after the whole payout has been prepared.
        RequireMaxLength(paidBy, 200, "Paid By");
        RequireMaxLength(payeeName, 200, "Paid To (name)");
        RequireMaxLength(request.PayeeContact?.Trim(), 40, "Payee contact");
        RequireMaxLength(request.PayeeEmail?.Trim(), 200, "Payee email");
        RequireMaxLength(request.PayeeAddress?.Trim(), 500, "Payee address");

        var ids = (request.CommissionIds ?? new List<Guid>()).Distinct().ToList();
        if (ids.Count == 0)
            throw new ValidationException("Select at least one commission to pay.");

        var rows = await _context.ReferralCommissions
            .Where(c => ids.Contains(c.Id) && c.HospitalId == hospitalId && c.DeletedAt == null)
            .ToListAsync(ct);
        var byId = rows.ToDictionary(c => c.Id);

        // Patient-payment gate — only for commissions earned on an appointment (a
        // manual/legacy commission has no patient bill to wait for).
        var payability = await PatientPaymentStatus.ForCommissionsAsync(
            _context, hospitalId,
            rows.Where(c => c.AppointmentId.HasValue)
                .Select(c => (c.Id, c.AppointmentId, c.ReferenceNumber)).ToList(),
            ct);

        var paid = new List<Guid>();
        var skipped = new List<SkippedCommission>();
        var now = DateTime.UtcNow;
        var actor = await CommissionActor.ResolveAsync(_context, ct);
        decimal total = 0m;

        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var c))
            {
                skipped.Add(new SkippedCommission(id, "Commission not found."));
                continue;
            }
            if (CommissionStatus.IsPaid(c.Status))
            {
                skipped.Add(new SkippedCommission(id, "Already paid."));
                continue;
            }
            if (CommissionStatus.IsCancelled(c.Status))
            {
                skipped.Add(new SkippedCommission(id, "Cancelled commissions cannot be paid."));
                continue;
            }
            if (c.CommissionAmount <= 0m)
            {
                skipped.Add(new SkippedCommission(id, "Only a positive commission amount can be paid."));
                continue;
            }
            if (c.AppointmentId.HasValue)
            {
                payability.TryGetValue(c.Id, out var patientStatus);
                if (!PatientPaymentStatus.IsPayable(patientStatus))
                {
                    skipped.Add(new SkippedCommission(id, "The patient has not paid yet."));
                    continue;
                }
            }

            c.Status = CommissionStatus.Paid;
            c.PaymentDate = now;
            c.PaidBy = paidBy;
            c.PayeeName = payeeName;
            c.UpdatedBy = actor;
            if (!string.IsNullOrWhiteSpace(request.PayeeContact)) c.PayeeContact = request.PayeeContact.Trim();
            if (!string.IsNullOrWhiteSpace(request.PayeeEmail)) c.PayeeEmail = request.PayeeEmail.Trim();
            if (!string.IsNullOrWhiteSpace(request.PayeeAddress)) c.PayeeAddress = request.PayeeAddress.Trim();
            c.UpdatedAt = now;

            paid.Add(id);
            total += c.CommissionAmount;
        }

        decimal recovered = 0m;
        var splitReferrers = new HashSet<Guid>();
        if (paid.Count > 0 && request.NetDeficits)
            recovered = await RecoverDeficitsAsync(rows.Where(c => paid.Contains(c.Id)).ToList(), paidBy, payeeName, actor, now, hospitalId, splitReferrers, ct);

        if (paid.Count > 0)
        {
            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Status is the concurrency token: another payout, write-off or edit reached one of these
                // rows (or a deficit being netted) between our read and this save. Nothing of ours was written.
                throw new ConflictException("Another payout just changed some of these commissions. Refresh the Referral Hub to see what is still unpaid, then try again.");
            }
        }

        // A split adds a row (its running total starts at 0) — re-base those partners.
        if (splitReferrers.Count > 0)
        {
            foreach (var referrerId in splitReferrers)
                await ReferralLedger.RecomputeAccumulatedTotal(_context, referrerId, hospitalId, ct);
            try
            {
                await _context.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The payout above is already committed; a running total is only a derived figure and is
                // re-stamped by the next commission change, so it must not fail a completed payout.
            }
        }

        return new PayReferralCommissionsResult(paid, skipped, total, recovered, total - recovered);
    }

    private static void RequireMaxLength(string? value, int max, string label)
    {
        if (value != null && value.Length > max)
            throw new ValidationException($"{label} is too long (at most {max} characters).");
    }

    /// <summary>
    /// Settles the partner's open deficit (negative UNPAID rows) against the gross of
    /// the rows just paid. Per partner (merge-resolved) so a duplicate's deficit is
    /// netted against the primary's payout. Returns the amount recovered.
    /// </summary>
    private async Task<decimal> RecoverDeficitsAsync(
        List<Domain.Entities.ReferralCommission> paidRows, string paidBy, string payeeName, string actor, DateTime now, Guid hospitalId, HashSet<Guid> splitReferrers, CancellationToken ct)
    {
        var registry = await _context.Referrers.AsNoTracking()
            .Where(r => r.HospitalId == hospitalId)
            .Select(r => new { r.ReferrerId, r.MergedIntoId })
            .ToListAsync(ct);
        var mergeMap = registry.ToDictionary(r => r.ReferrerId, r => r.MergedIntoId);
        Guid Root(Guid id)
        {
            var seen = new HashSet<Guid>();
            while (mergeMap.TryGetValue(id, out var next) && next.HasValue && seen.Add(id)) id = next.Value;
            return id;
        }

        decimal recoveredTotal = 0m;
        foreach (var group in paidRows.GroupBy(c => Root(c.ReferrerId)))
        {
            var capacity = group.Sum(c => c.CommissionAmount);          // gross paid to this partner
            if (capacity <= 0m) continue;

            var aliasIds = registry.Where(r => Root(r.ReferrerId) == group.Key).Select(r => r.ReferrerId).ToList();
            var deficits = await _context.ReferralCommissions
                .Where(c => c.HospitalId == hospitalId && c.DeletedAt == null && c.CommissionAmount < 0m
                            && aliasIds.Contains(c.ReferrerId)
                            && c.Status != CommissionStatus.Paid
                            && c.Status != CommissionStatus.Cancelled && c.Status != "CANCELLED")
                .OrderBy(c => c.ServiceDate).ThenBy(c => c.TransactionDate).ThenBy(c => c.Id)
                .ToListAsync(ct);

            foreach (var d in deficits)
            {
                if (capacity <= 0m) break;
                var owed = -d.CommissionAmount;
                var take = Math.Min(owed, capacity);

                if (take < owed)
                {
                    // Only part of this deficit can be recovered: split it. The recovered
                    // slice is settled below; the remainder stays outstanding. The
                    // remainder is detached from any service line (clawback rows sit
                    // outside the one-live-commission-per-service index).
                    _context.ReferralCommissions.Add(new Domain.Entities.ReferralCommission
                    {
                        HospitalId = hospitalId,
                        ReferrerId = d.ReferrerId,
                        ReferrerName = d.ReferrerName,
                        Modality = d.Modality,
                        AppointmentId = d.AppointmentId,
                        AppointmentServiceId = null,
                        ReferenceNumber = d.ReferenceNumber,
                        CommissionAmount = -(owed - take),
                        Status = CommissionStatus.Unpaid,
                        TransactionDate = d.TransactionDate,
                        ServiceDate = d.ServiceDate,
                        UpdatedBy = actor,
                        Remarks = $"[Remainder of a deficit after ₹{take:0.##} was recovered from a payout on {now:yyyy-MM-dd}] " + d.Remarks,
                    });
                    d.CommissionAmount = -take;
                    splitReferrers.Add(d.ReferrerId);
                }

                d.Status = CommissionStatus.Paid;       // settled = recovered
                d.PaymentDate = now;
                d.PaidBy = paidBy;
                d.PayeeName = payeeName;
                d.UpdatedBy = actor;
                d.Remarks = (d.Remarks ?? string.Empty) + $" [Recovered ₹{take:0.##} by netting against a payout on {now:yyyy-MM-dd}]";
                d.UpdatedAt = now;

                capacity -= take;
                recoveredTotal += take;
            }
        }
        return recoveredTotal;
    }
}
