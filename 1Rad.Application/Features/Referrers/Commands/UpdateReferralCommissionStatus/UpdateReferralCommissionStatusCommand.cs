using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.UpdateReferralCommissionStatus;

public record UpdateReferralCommissionStatusCommand(
    Guid CommissionId,
    string Status
) : IRequest<bool>;

public class UpdateReferralCommissionStatusCommandHandler : IRequestHandler<UpdateReferralCommissionStatusCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public UpdateReferralCommissionStatusCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(UpdateReferralCommissionStatusCommand request, CancellationToken cancellationToken)
    {
        var commission = await _context.ReferralCommissions
            .FirstOrDefaultAsync(c => c.Id == request.CommissionId, cancellationToken);

        if (commission == null)
            throw new Exception($"FISCAL ERROR: Commission record [{request.CommissionId}] not found in strategic ledger.");

        commission.Status = request.Status;
        if (request.Status == "PAID")
        {
            commission.PaymentDate = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
