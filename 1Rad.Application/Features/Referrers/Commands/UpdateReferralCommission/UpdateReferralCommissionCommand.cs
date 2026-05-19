using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.UpdateReferralCommission;

public record UpdateReferralCommissionCommand(
    Guid CommissionId,
    decimal Amount,
    string Modality,
    string? ReferenceNumber,
    string? Remarks,
    string Status
) : IRequest<bool>;

public class UpdateReferralCommissionCommandHandler : IRequestHandler<UpdateReferralCommissionCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public UpdateReferralCommissionCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(UpdateReferralCommissionCommand request, CancellationToken cancellationToken)
    {
        var commission = await _context.ReferralCommissions
            .FirstOrDefaultAsync(c => c.Id == request.CommissionId, cancellationToken);

        if (commission == null)
            throw new Exception($"FISCAL ERROR: Commission record [{request.CommissionId}] not found for modification.");

        commission.CommissionAmount = request.Amount;
        commission.Modality = request.Modality;
        commission.ReferenceNumber = request.ReferenceNumber;
        commission.Remarks = request.Remarks;
        commission.Status = request.Status;

        if (request.Status == "PAID" && commission.PaymentDate == null)
        {
            commission.PaymentDate = DateTime.UtcNow;
        }

        // Save current changes first
        await _context.SaveChangesAsync(cancellationToken);

        // Recalculate Accumulated Total chronologically for this referrer to prevent drift
        var allCommissions = await _context.ReferralCommissions
            .Where(c => c.ReferrerId == commission.ReferrerId && c.HospitalId == commission.HospitalId)
            .OrderBy(c => c.TransactionDate)
            .ToListAsync(cancellationToken);

        decimal runningTotal = 0;
        foreach (var c in allCommissions)
        {
            runningTotal += c.CommissionAmount;
            c.AccumulatedTotal = runningTotal;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
