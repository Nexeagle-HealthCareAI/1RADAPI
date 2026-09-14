using _1Rad.Application.Common;
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
            throw new Exception($"TACTICAL FAILURE: Referrer identity [{request.ReferrerId}] not recognized in global registry for current facility.");

        var lines = (request.Lines ?? new List<CommissionLine>())
            .Where(l => l != null && !string.IsNullOrWhiteSpace(l.Modality))
            .ToList();
        if (lines.Count == 0)
            throw new Exception("PROTOCOL FAILURE: At least one service line is required to record a payout.");

        // Existing (non-deleted) commissions tied to this invoice reference.
        var existing = new List<ReferralCommission>();
        if (!string.IsNullOrEmpty(request.ReferenceNumber))
        {
            existing = await _context.ReferralCommissions
                .Where(c => c.ReferenceNumber == request.ReferenceNumber
                            && c.HospitalId == hospitalId
                            && c.DeletedAt == null)
                .ToListAsync(cancellationToken);
        }

        var now = DateTime.UtcNow;
        var resultIds = new List<Guid>();
        var matched = new HashSet<Guid>();

        foreach (var line in lines)
        {
            var modality = line.Modality.Trim();
            // A payout line can never be negative — floor at zero so a bad input
            // can't produce a negative commission / negative payout total.
            var lineAmount = Math.Max(0m, line.Amount);
            // Match an existing line by modality (case-insensitive) so re-saving
            // the same payout updates in place rather than duplicating.
            var commission = existing.FirstOrDefault(c =>
                !matched.Contains(c.Id) &&
                string.Equals(c.Modality, modality, StringComparison.OrdinalIgnoreCase));

            if (commission != null)
            {
                // Real money has already been disbursed for this line — a stale
                // client cache (e.g. a payout drawer that didn't know this
                // modality was already settled) must not silently overwrite or
                // erase that history. Mirrors the same guard UpdateReferralCommissionCommand
                // already enforces for the single-row edit path.
                if (string.Equals(commission.Status, "PAID", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"The commission for modality '{modality}' on this invoice is already paid and cannot be modified. Submit an approval request for an adjustment.");

                matched.Add(commission.Id);
                commission.CommissionAmount = lineAmount;
                commission.Status = line.Status ?? commission.Status;
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
                    Status = line.Status ?? "UNPAID",
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
        foreach (var stale in existing.Where(c => !matched.Contains(c.Id)
                                                   && !string.Equals(c.Status, "PAID", StringComparison.OrdinalIgnoreCase)))
        {
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
