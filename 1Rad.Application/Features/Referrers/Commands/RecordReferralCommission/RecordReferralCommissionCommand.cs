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

        // Upsert Logic: If reference exists, update instead of adding
        ReferralCommission? commission = null;
        if (!string.IsNullOrEmpty(request.ReferenceNumber))
        {
            commission = await _context.ReferralCommissions
                .FirstOrDefaultAsync(c => c.ReferenceNumber == request.ReferenceNumber && c.HospitalId == hospitalId, cancellationToken);
        }

        if (commission != null)
        {
            // Update Existing
            commission.CommissionAmount = request.Amount;
            commission.Modality = request.Modality;
            commission.PatientName = request.PatientName ?? commission.PatientName;
            commission.Remarks = (commission.Remarks ?? "") + $" [Updated: ₹{request.Amount}]";
            commission.Status = request.Status ?? commission.Status;
            commission.TransactionDate = DateTime.UtcNow;
        }
        else
        {
            commission = new ReferralCommission
            {
                ReferrerId = request.ReferrerId,
                ReferrerName = referrer.Name ?? "Unknown",
                Modality = request.Modality,
                PatientName = request.PatientName ?? "N/A",
                CommissionAmount = request.Amount,
                AccumulatedTotal = 0, // Will be calculated below
                TransactionDate = DateTime.UtcNow,
                Status = request.Status ?? "UNPAID",
                ReferenceNumber = request.ReferenceNumber,
                Remarks = request.Remarks,
                HospitalId = hospitalId
            };
            _context.ReferralCommissions.Add(commission);
        }

        // Save first so new/updated records are in the DB before recalculation
        await _context.SaveChangesAsync(cancellationToken);

        // Recalculate Accumulated Total chronologically for this referrer to prevent drift
        var allCommissions = await _context.ReferralCommissions
            .Where(c => c.ReferrerId == request.ReferrerId && c.HospitalId == hospitalId)
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
