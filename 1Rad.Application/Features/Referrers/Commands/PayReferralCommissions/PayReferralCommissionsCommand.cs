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
/// </summary>
public record PayReferralCommissionsCommand(
    List<Guid> CommissionIds,
    string PaidBy,
    string PayeeName,
    string? PayeeContact = null,
    string? PayeeEmail = null,
    string? PayeeAddress = null
) : IRequest<PayReferralCommissionsResult>;

public record SkippedCommission(Guid CommissionId, string Reason);

public record PayReferralCommissionsResult(
    List<Guid> Paid,
    List<SkippedCommission> Skipped,
    decimal TotalPaid);

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
            if (!string.IsNullOrWhiteSpace(request.PayeeContact)) c.PayeeContact = request.PayeeContact.Trim();
            if (!string.IsNullOrWhiteSpace(request.PayeeEmail)) c.PayeeEmail = request.PayeeEmail.Trim();
            if (!string.IsNullOrWhiteSpace(request.PayeeAddress)) c.PayeeAddress = request.PayeeAddress.Trim();
            c.UpdatedAt = now;

            paid.Add(id);
            total += c.CommissionAmount;
        }

        if (paid.Count > 0)
            await _context.SaveChangesAsync(ct);

        return new PayReferralCommissionsResult(paid, skipped, total);
    }
}
