using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.RecordReferralCommission;

public record RecordReferralCommissionCommand(
    Guid ReferrerId,
    decimal Amount,
    string Modality,
    string? ReferenceNumber,
    string? Remarks,
    string? PatientName = null,
    string? Status = "UNPAID"
) : IRequest<Guid>;


public class RecordReferralCommissionCommandHandler : IRequestHandler<RecordReferralCommissionCommand, Guid>
{
    private readonly IApplicationDbContext _context;

    public RecordReferralCommissionCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Guid> Handle(RecordReferralCommissionCommand request, CancellationToken cancellationToken)
    {
        var referrer = await _context.Referrers
            .FirstOrDefaultAsync(r => r.ReferrerId == request.ReferrerId, cancellationToken);

        if (referrer == null)
            throw new Exception($"TACTICAL FAILURE: Referrer identity [{request.ReferrerId}] not recognized in global registry for current facility.");

        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty)
            throw new Exception("FISCAL ERROR: Security context failure. Hospital identity is required for commission logging.");

        // Appointment-generated commission rows are reconciled exclusively by
        // the appointment lifecycle. A manual record must never overwrite one
        // merely because it shares an invoice/reference number.
        ReferralCommission? commission = null;
        if (!string.IsNullOrEmpty(request.ReferenceNumber))
        {
            commission = await _context.ReferralCommissions
                .FirstOrDefaultAsync(c => c.ReferenceNumber == request.ReferenceNumber && c.HospitalId == hospitalId, cancellationToken);
        }

        if (commission != null)
            throw new InvalidOperationException("A commission already exists for this reference. Use the approved commission adjustment workflow.");

        if (request.Amount <= 0)
            throw new ArgumentException("Commission amount must be greater than zero.", nameof(request.Amount));

        if (string.Equals(request.Status, "PAID", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("New commissions must start as UNPAID and be marked paid through the payout workflow.");

        var amount = request.Amount;

        commission = new ReferralCommission
        {
            ReferrerId = request.ReferrerId,
            ReferrerName = referrer.Name ?? "Unknown",
            Modality = request.Modality,
            PatientName = request.PatientName ?? "N/A",
            CommissionAmount = amount,
            AccumulatedTotal = 0,
            TransactionDate = DateTime.UtcNow,
            Status = "UNPAID",
            ReferenceNumber = request.ReferenceNumber,
            Remarks = request.Remarks,
            HospitalId = hospitalId
        };
        _context.ReferralCommissions.Add(commission);

        // Save first so new/updated records are in the DB before recalculation
        await _context.SaveChangesAsync(cancellationToken);

        // Recalculate Accumulated Total chronologically for this referrer to prevent drift
        var allCommissions = await _context.ReferralCommissions
            .Where(c => c.ReferrerId == request.ReferrerId
                     && c.HospitalId == hospitalId
                     && c.DeletedAt == null)
            .OrderBy(c => c.TransactionDate)
            .ToListAsync(cancellationToken);

        decimal runningTotal = 0;
        foreach (var c in allCommissions)
        {
            runningTotal += c.CommissionAmount;
            c.AccumulatedTotal = runningTotal;
        }


        await _context.SaveChangesAsync(cancellationToken);


        return commission.Id;
    }
}
