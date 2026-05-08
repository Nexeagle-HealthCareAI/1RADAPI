using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.RecordReferralCommission;

public record RecordReferralCommissionCommand(
    Guid ReferrerId,
    decimal Amount,
    string Modality,
    string? Remarks
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
            throw new Exception("TACTICAL FAILURE: Referrer identity not recognized in global registry.");

        var hospitalId = _context.UserContext.HospitalId;

        // Calculate accumulated total for this referrer in this hospital context
        var currentTotal = await _context.ReferralCommissions
            .Where(c => c.ReferrerId == request.ReferrerId && c.HospitalId == hospitalId)
            .SumAsync(c => c.CommissionAmount, cancellationToken);

        var commission = new ReferralCommission
        {
            ReferrerId = request.ReferrerId,
            ReferrerName = referrer.Name ?? "Unknown",
            Modality = request.Modality,
            CommissionAmount = request.Amount,
            AccumulatedTotal = currentTotal + request.Amount,
            TransactionDate = DateTime.UtcNow,
            Status = "Pending",
            Remarks = request.Remarks,
            HospitalId = hospitalId
        };

        _context.ReferralCommissions.Add(commission);
        await _context.SaveChangesAsync(cancellationToken);

        return commission.Id;
    }
}
