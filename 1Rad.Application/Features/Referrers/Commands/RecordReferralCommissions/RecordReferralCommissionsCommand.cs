using _1Rad.Application.Common;
using _1Rad.Domain.Exceptions;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.RecordReferralCommissions;

/// <summary>
/// Records a referral payout as one commission row PER SERVICE LINE so a
/// multi-service visit (e.g. MRI + CT + USG) pays the referrer per modality
/// instead of collapsing everything into a single line. The set of lines is
/// authoritative for the given <see cref="ReferenceNumber"/>: matched lines are
/// updated, new ones inserted, and previously-recorded lines that are no longer
/// present are soft-deleted.
/// </summary>
public record CommissionLine(
    string Modality,
    decimal Amount,
    string? Status = "UNPAID",
    Guid? AppointmentServiceId = null
);

public record RecordReferralCommissionsCommand(
    Guid ReferrerId,
    string? ReferenceNumber,
    string? Remarks,
    string? PatientName,
    Guid? AppointmentId,
    List<CommissionLine> Lines
) : IRequest<List<Guid>>;

public class RecordReferralCommissionsCommandHandler : IRequestHandler<RecordReferralCommissionsCommand, List<Guid>>
{
    private readonly IApplicationDbContext _context;

    public RecordReferralCommissionsCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<Guid>> Handle(RecordReferralCommissionsCommand request, CancellationToken cancellationToken)
    {
        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty)
            throw new Exception("FISCAL ERROR: Security context failure. Hospital identity is required for commission logging.");

        var referrer = await _context.Referrers
            .FirstOrDefaultAsync(r => r.ReferrerId == request.ReferrerId && r.HospitalId == hospitalId, cancellationToken);
        if (referrer == null)
            throw new NotFoundException($"Referrer identity [{request.ReferrerId}] was not found for this facility.");

        var lines = (request.Lines ?? new List<CommissionLine>())
            .Where(l => l != null && !string.IsNullOrWhiteSpace(l.Modality))
            .ToList();
        if (lines.Count == 0)
            throw new ValidationException("At least one service line is required to record a payout.");

        // Existing (non-deleted) commissions tied to this invoice reference.
        var existing = new List<ReferralCommission>();
        if (!string.IsNullOrEmpty(request.ReferenceNumber))
        {
            // Only THIS referrer's rows (review R7): an invoice can carry commissions for
            // more than one partner, and recording A's payout used to overwrite or delete B's.
            existing = await _context.ReferralCommissions
                .Where(c => c.ReferenceNumber == request.ReferenceNumber
                            && c.ReferrerId == request.ReferrerId
                            && c.HospitalId == hospitalId
                            && c.DeletedAt == null)
                .ToListAsync(cancellationToken);
        }

        var now = DateTime.UtcNow;
        var resultIds = new List<Guid>();
        var matched = new HashSet<Guid>();

        // Server-side payout cap. The drawer stops a commission from exceeding its
        // service charge, but that was a browser-only check; enforce it here for
        // every line tied to a service.
        var serviceIds = lines.Where(l => l.AppointmentServiceId.HasValue).Select(l => l.AppointmentServiceId!.Value).Distinct().ToList();
        var serviceAmounts = serviceIds.Count == 0
            ? new Dictionary<Guid, (decimal Amount, string Name)>()
            : (await _context.AppointmentServices.AsNoTracking()
                .Where(s => serviceIds.Contains(s.Id) && s.HospitalId == hospitalId)
                .Select(s => new { s.Id, s.Amount, s.ServiceName })
                .ToListAsync(cancellationToken))
              .ToDictionary(s => s.Id, s => (s.Amount, s.ServiceName ?? string.Empty));

        foreach (var line in lines)
        {
            var modality = line.Modality.Trim();
            // A payout line can never be negative — floor at zero so a bad input
            // can't produce a negative commission / negative payout total.
            var lineAmount = Math.Max(0m, line.Amount);

            if (line.AppointmentServiceId.HasValue
                && serviceAmounts.TryGetValue(line.AppointmentServiceId.Value, out var svc)
                && svc.Amount > 0m && lineAmount > svc.Amount)
                throw new BusinessRuleViolationException(
                    $"Commission for '{modality}' (₹{lineAmount:0.##}) cannot exceed the service amount of ₹{svc.Amount:0.##}.");

            // Status is free text on the wire — accept only the known spellings and
            // store the canonical one. null means "leave an existing row's status".
            string? lineStatus = null;
            if (line.Status != null)
            {
                lineStatus = CommissionStatus.Normalize(line.Status)
                    ?? throw new ValidationException($"Unknown commission status '{line.Status}'.");
                // Paying goes through the payout screen (review R6), which records who
                // paid whom, checks the patient has paid and nets any deficit.
                if (lineStatus == CommissionStatus.Paid)
                    throw new BusinessRuleViolationException(
                        "Save the commission first, then pay it from the payout screen.", "COMMISSION_PAY_VIA_PAYOUT");
            }

            // Match an existing line to update in place (re-saving the same payout
            // must not duplicate it). The service line is the precise key — two CT
            // services on one visit share a modality, so matching on modality alone
            // pairs them by list order. Fall back to modality for legacy rows that
            // carry no service id.
            var commission =
                (line.AppointmentServiceId.HasValue
                    ? existing.FirstOrDefault(c => !matched.Contains(c.Id) && c.AppointmentServiceId == line.AppointmentServiceId)
                    : null)
                ?? existing.FirstOrDefault(c =>
                    !matched.Contains(c.Id) &&
                    (!line.AppointmentServiceId.HasValue || c.AppointmentServiceId == null) &&
                    string.Equals(c.Modality, modality, StringComparison.OrdinalIgnoreCase));

            if (commission != null)
            {
                // Real money has already been disbursed for this line — a stale
                // client cache (e.g. a payout drawer that didn't know this
                // modality was already settled) must not silently overwrite or
                // erase that history. Mirrors the same guard UpdateReferralCommissionCommand
                // already enforces for the single-row edit path.
                if (string.Equals(commission.Status, "PAID", StringComparison.OrdinalIgnoreCase))
                    throw new BusinessRuleViolationException(
                        $"The commission for modality '{modality}' on this invoice is already paid and cannot be modified. Submit an approval request for an adjustment.");

                // A commission generated from the visit's services follows the visit
                // (edits, discounts, approvals); this manual save must not rewrite it.
                if (commission.AppointmentServiceId != null && Math.Abs(commission.CommissionAmount - lineAmount) >= 0.005m)
                    throw new BusinessRuleViolationException(
                        $"The {modality} commission comes from the visit's services. Change it with an approval request instead.",
                        "COMMISSION_OWNED_BY_VISIT");

                matched.Add(commission.Id);
                commission.CommissionAmount = lineAmount;
                if (lineStatus != null)
                {
                    if (lineStatus == CommissionStatus.Paid && !CommissionStatus.IsPaid(commission.Status))
                        commission.PaymentDate = now;
                    commission.Status = lineStatus;
                }
                commission.AppointmentServiceId = line.AppointmentServiceId ?? commission.AppointmentServiceId;
                commission.AppointmentId = request.AppointmentId ?? commission.AppointmentId;
                commission.PatientName = request.PatientName ?? commission.PatientName;
                commission.Remarks = request.Remarks ?? commission.Remarks;
                commission.UpdatedAt = now;
            }
            else
            {
                commission = new ReferralCommission
                {
                    ReferrerId = request.ReferrerId,
                    ReferrerName = referrer.Name ?? "Unknown",
                    Modality = modality,
                    PatientName = request.PatientName ?? "N/A",
                    AppointmentId = request.AppointmentId,
                    AppointmentServiceId = line.AppointmentServiceId,
                    CommissionAmount = lineAmount,
                    AccumulatedTotal = 0,
                    TransactionDate = now,
                    // A write-off is booked directly as PAID (it settles a deficit); it is
                    // stamped with a payment date like any other settled row.
                    Status = lineStatus ?? CommissionStatus.Unpaid,
                    PaymentDate = lineStatus == CommissionStatus.Paid ? now : null,
                    ReferenceNumber = request.ReferenceNumber,
                    Remarks = request.Remarks,
                    HospitalId = hospitalId,
                    UpdatedAt = now
                };
                _context.ReferralCommissions.Add(commission);
            }

            resultIds.Add(commission.Id);
        }

        // Lines removed from the payout — soft-delete so reporting/sync stay consistent.
        // A PAID row is settlement history, not a draft line the client can drop —
        // an incoming payload that simply omits it (stale cache, edited elsewhere)
        // must never make it disappear from the ledger.
        // Commissions generated from a visit's services are owned by that visit
        // (review R7) — this manual-payout save must not tombstone them.
        foreach (var stale in existing.Where(c => !matched.Contains(c.Id)
                                                   && c.AppointmentServiceId == null
                                                   && !string.Equals(c.Status, "PAID", StringComparison.OrdinalIgnoreCase)))
        {
            // Zero the amount before tombstoning — every other soft-delete site for
            // this entity (DeleteInvoiceCommand, UpdateAppointmentCommand,
            // ReferrerReassign) does the same, because a handful of reads key off
            // DeletedAt alone. Leaving CommissionAmount nonzero here let a removed
            // payout line keep counting in those reads (e.g. GetFinancialMatrixQuery's
            // Physician ROI Ledger) forever after being tombstoned everywhere else.
            stale.CommissionAmount = 0;
            stale.DeletedAt = now;
            stale.UpdatedAt = now;
        }

        await _context.SaveChangesAsync(cancellationToken);

        // Recalculate accumulated totals chronologically for this referrer.
        await ReferralLedger.RecomputeAccumulatedTotal(_context, request.ReferrerId, hospitalId, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        return resultIds;
    }
}
