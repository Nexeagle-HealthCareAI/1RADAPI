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
    bool NetDeficits = false,
    // Review R10/R12: how the money went and the tax deducted from it.
    // TdsCategory: COMMISSION / PROFESSIONAL / NONE (older clients send nothing = NONE).
    string? TdsCategory = null,
    string? PayeePan = null,
    string? Mode = null,
    string? Reference = null,
    // True = work out the numbers (TDS, net) without recording anything.
    bool PreviewOnly = false
) : IRequest<PayReferralCommissionsResult>;

/// <summary>One payout per partner in the request: what changed hands.</summary>
public record PayoutSummary(Guid? PayoutId, string? PayoutNumber, Guid ReferrerId, string PayeeName, string? PayeePan,
    decimal Gross, decimal DeficitRecovered, string TdsCategory, decimal TdsRate, decimal TdsAmount, decimal Net,
    decimal YearTotal, string TdsNote);

public record SkippedCommission(Guid CommissionId, string Reason);

/// <param name="TotalPaid">Gross of the commissions marked paid.</param>
/// <param name="DeficitRecovered">Deficit recovered out of this payout (0 unless NetDeficits).</param>
/// <param name="NetPaid">Cash to hand over: TotalPaid − DeficitRecovered.</param>
public record PayReferralCommissionsResult(
    List<Guid> Paid,
    List<SkippedCommission> Skipped,
    decimal TotalPaid,
    decimal DeficitRecovered = 0m,
    decimal NetPaid = 0m,
    decimal TdsDeducted = 0m,
    List<PayoutSummary>? Payouts = null,
    List<string>? Notices = null,
    bool Preview = false);

public class PayReferralCommissionsCommandHandler : IRequestHandler<PayReferralCommissionsCommand, PayReferralCommissionsResult>
{
    private readonly IApplicationDbContext _context;
    private readonly Microsoft.Extensions.Configuration.IConfiguration? _configuration;

    public PayReferralCommissionsCommandHandler(IApplicationDbContext context,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
    {
        _context = context;
        _configuration = configuration;
    }

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
        var recoveredByRoot = new Dictionary<Guid, decimal>();
        if (paid.Count > 0 && request.NetDeficits)
            recovered = await RecoverDeficitsAsync(rows.Where(c => paid.Contains(c.Id)).ToList(), paidBy, payeeName, actor, now, hospitalId, splitReferrers, ct, recoveredByRoot);

        // Review R10/R12: one payout header per partner, with TDS worked out against
        // what the partner has already been paid this financial year.
        var (payouts, notices) = paid.Count > 0
            ? await BuildPayoutsAsync(rows.Where(c => paid.Contains(c.Id)).ToList(), recoveredByRoot, request, paidBy, payeeName, now, hospitalId, ct)
            : (new List<PayoutSummary>(), new List<string>());
        var tdsTotal = payouts.Sum(p => p.TdsAmount);

        if (request.PreviewOnly)
            return new PayReferralCommissionsResult(paid, skipped, total, recovered, total - recovered - tdsTotal, tdsTotal, payouts, notices, Preview: true);

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

        return new PayReferralCommissionsResult(paid, skipped, total, recovered, total - recovered - tdsTotal, tdsTotal, payouts, notices);
    }

    private async Task<(List<PayoutSummary>, List<string>)> BuildPayoutsAsync(
        List<Domain.Entities.ReferralCommission> paidRows, Dictionary<Guid, decimal> recoveredByRoot,
        PayReferralCommissionsCommand request, string paidBy, string payeeName, DateTime now, Guid hospitalId, CancellationToken ct)
    {
        var registry = await _context.Referrers
            .Where(r => r.HospitalId == hospitalId)
            .ToListAsync(ct);
        var byId = registry.ToDictionary(r => r.ReferrerId);
        Guid Root(Guid id)
        {
            var seen = new HashSet<Guid>();
            while (byId.TryGetValue(id, out var r) && r.MergedIntoId.HasValue && seen.Add(id)) id = r.MergedIntoId.Value;
            return id;
        }

        var category = (request.TdsCategory ?? ReferralTds.None).Trim().ToUpperInvariant();
        var rule = ReferralTds.RuleFor(category, _configuration);
        if (category != ReferralTds.None && rule == null)
            throw new ValidationException("TDS category must be COMMISSION, PROFESSIONAL or NONE.");
        var mode = string.IsNullOrWhiteSpace(request.Mode) ? "CASH" : request.Mode.Trim().ToUpperInvariant();
        if (mode.Length > 20) throw new ValidationException("Payment mode is too long.");
        var reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        if (reference?.Length > 100) throw new ValidationException("Reference is too long (at most 100 characters).");
        string? panFromRequest = null;
        if (!string.IsNullOrWhiteSpace(request.PayeePan))
        {
            panFromRequest = request.PayeePan.Trim().ToUpperInvariant();
            if (!ReferralTds.IsValidPan(panFromRequest)) throw new ValidationException("PAN should look like ABCDE1234F.");
        }

        var fy = InvoiceNumbering.FinancialYearLabel(now);
        var (fyStart, fyEnd) = FinancialYearBounds(fy);
        var payouts = new List<PayoutSummary>();
        var notices = new List<string>();

        foreach (var group in paidRows.GroupBy(c => Root(c.ReferrerId)))
        {
            var rootId = group.Key;
            byId.TryGetValue(rootId, out var partner);
            var aliasIds = registry.Where(r => Root(r.ReferrerId) == rootId).Select(r => r.ReferrerId).ToList();
            if (partner != null && panFromRequest != null && partner.Pan != panFromRequest && !request.PreviewOnly)
                partner.Pan = panFromRequest; // remember it for next time
            var pan = panFromRequest ?? partner?.Pan;

            var gross = group.Sum(c => c.CommissionAmount);
            recoveredByRoot.TryGetValue(rootId, out var rec);
            var taxable = Math.Max(0, gross - rec);

            var earlier = await _context.ReferralPayouts.AsNoTracking()
                .Where(p => p.HospitalId == hospitalId && p.ReferrerId == rootId && p.FinancialYear == fy)
                .Select(p => new { Taxable = p.Gross - p.DeficitRecovered, p.TdsAmount })
                .ToListAsync(ct);
            // Paid before payout headers existed (this year): counted, never taxed.
            var legacy = await _context.ReferralCommissions.AsNoTracking()
                .Where(c => c.HospitalId == hospitalId && aliasIds.Contains(c.ReferrerId) && c.PayoutId == null
                            && c.Status == CommissionStatus.Paid && c.CommissionAmount > 0
                            && c.PaymentDate >= fyStart && c.PaymentDate < fyEnd)
                .SumAsync(c => c.CommissionAmount, ct);
            var yearBefore = earlier.Sum(e => e.Taxable) + legacy;
            var yearTaxed = earlier.Where(e => e.TdsAmount > 0).Sum(e => e.Taxable);

            var tds = ReferralTds.Compute(rule, ReferralTds.IsValidPan(pan), yearBefore, yearTaxed, taxable, _configuration);
            var net = taxable - tds.Tds;

            if (partner?.IsDoctor == true && category == ReferralTds.Commission)
                notices.Add($"{partner.Name} is recorded as a doctor. Paying a doctor for referring patients breaches the NMC's code of conduct, and the Supreme Court has held such payments are not an allowable business expense (Apex Laboratories, 2022). Check with your CA before paying.");
            if (rule != null && tds.OverThreshold && !ReferralTds.IsValidPan(pan))
                notices.Add($"No PAN on file for {partner?.Name ?? payeeName}: TDS is at the higher {tds.Rate:0.##}% rate. Add the PAN to use the normal rate.");

            Guid? payoutId = null;
            string? number = null;
            if (!request.PreviewOnly)
            {
                var seq = await _context.NextSequenceValueAsync(hospitalId, $"REFERRAL_PAYOUT_FY_{fy}", 1, ct);
                number = $"RPO{InvoiceNumbering.Compact(fy)}-{seq:D5}";
                var payout = new Domain.Entities.ReferralPayout
                {
                    HospitalId = hospitalId,
                    PayoutNumber = number,
                    ReferrerId = rootId,
                    PayeeName = payeeName,
                    PayeePan = ReferralTds.IsValidPan(pan) ? pan : null,
                    FinancialYear = fy,
                    PaidAt = now,
                    PaidBy = paidBy,
                    CreatedByUserId = _context.UserContext.UserId == Guid.Empty ? null : _context.UserContext.UserId,
                    Mode = mode,
                    Reference = reference,
                    Gross = gross,
                    DeficitRecovered = rec,
                    TdsCategory = rule?.Code ?? ReferralTds.None,
                    TdsRate = tds.Rate,
                    TdsAmount = tds.Tds,
                    Net = net,
                    TdsNote = tds.Note.Length <= 500 ? tds.Note : tds.Note[..500],
                };
                _context.ReferralPayouts.Add(payout);
                payoutId = payout.Id;
                foreach (var c in group) c.PayoutId = payout.Id;
            }
            payouts.Add(new PayoutSummary(payoutId, number, rootId, payeeName, ReferralTds.IsValidPan(pan) ? pan : null,
                gross, rec, rule?.Code ?? ReferralTds.None, tds.Rate, tds.Tds, net, tds.YearTotal, tds.Note));
        }
        return (payouts, notices);
    }

    /// <summary>"2026-27" → [1 Apr 2026 00:00 IST, 1 Apr 2027 00:00 IST) in UTC.</summary>
    public static (DateTime Start, DateTime End) FinancialYearBounds(string fy)
    {
        var startYear = int.Parse(fy[..4]);
        var start = IstDateRange.ToUtcStart(new DateTime(startYear, 4, 1));
        return (start, start.AddYears(1));
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
        List<Domain.Entities.ReferralCommission> paidRows, string paidBy, string payeeName, string actor, DateTime now, Guid hospitalId, HashSet<Guid> splitReferrers, CancellationToken ct,
        Dictionary<Guid, decimal>? recoveredByRoot = null)
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
                if (recoveredByRoot != null)
                    recoveredByRoot[group.Key] = recoveredByRoot.GetValueOrDefault(group.Key) + take;
            }
        }
        return recoveredTotal;
    }
}
