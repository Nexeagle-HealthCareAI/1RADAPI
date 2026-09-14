using _1Rad.Application.Common;
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

        if (commission.AppointmentId.HasValue || commission.AppointmentServiceId.HasValue)
            throw new InvalidOperationException("Appointment-generated commissions can only be changed through an approved appointment or commission adjustment workflow.");
        if (string.Equals(commission.Status, "PAID", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A paid commission is immutable. Submit an approval request for an adjustment.");
        if (request.Amount <= 0)
            throw new ArgumentException("Commission amount must be greater than zero.", nameof(request.Amount));
        if (string.Equals(request.Status, "PAID", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use the payout workflow to mark a commission as paid.");

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
        await ReferralLedger.RecomputeAccumulatedTotal(_context, commission.ReferrerId, commission.HospitalId, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
