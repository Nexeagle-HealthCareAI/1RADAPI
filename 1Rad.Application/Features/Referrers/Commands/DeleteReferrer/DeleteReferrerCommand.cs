using _1Rad.Application.Common;
using _1Rad.Domain.Exceptions;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.DeleteReferrer;

public record DeleteReferrerCommand(Guid ReferrerId) : IRequest<bool>;

public class DeleteReferrerCommandHandler : IRequestHandler<DeleteReferrerCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public DeleteReferrerCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(DeleteReferrerCommand request, CancellationToken cancellationToken)
    {
        var hospitalId = _context.UserContext.HospitalId;

        var referrer = await _context.Referrers
            .FirstOrDefaultAsync(r => r.ReferrerId == request.ReferrerId && r.HospitalId == hospitalId, cancellationToken);

        if (referrer == null || referrer.DeletedAt != null) return false;

        // A partner with money still open (an unpaid payout owed to them, or a
        // clawback deficit they owe) cannot just vanish: the Referral Hub lists
        // partners from their live commissions, and a deleted partner's unpaid
        // rows would keep counting in every total with nobody left to pay or
        // recover them from. Settle (or merge) first.
        // (A partner already merged into another is just an alias: its balance is
        // carried by the primary it points at, so it is safe to remove — this is
        // what "merge and remove duplicate" does.)
        var hasOpenBalance = referrer.MergedIntoId == null
            && await _context.ReferralCommissions
            .AnyAsync(c => c.ReferrerId == request.ReferrerId
                           && c.HospitalId == hospitalId
                           && c.DeletedAt == null
                           && c.CommissionAmount != 0
                           && c.Status != CommissionStatus.Paid
                           && c.Status != CommissionStatus.Cancelled
                           && c.Status != "CANCELLED", cancellationToken);
        if (hasOpenBalance)
            throw new BusinessRuleViolationException(
                "This partner still has unpaid (or clawback) commissions. Settle them first, or merge this partner into another one instead of deleting.");

        // Partners merged INTO this one point at it through MergedIntoId; deleting
        // the root would leave them aliasing a tombstone.
        var hasAliases = await _context.Referrers
            .AnyAsync(r => r.MergedIntoId == request.ReferrerId && r.HospitalId == hospitalId && r.DeletedAt == null, cancellationToken);
        if (hasAliases)
            throw new BusinessRuleViolationException(
                "Other partners are merged into this one. Unmerge them first, or merge this partner into another one instead of deleting.");

        // Soft delete so the sync engine propagates the tombstone and historic
        // commissions keep their referrer reference for reporting.
        var now = DateTime.UtcNow;
        referrer.DeletedAt = now;
        referrer.UpdatedAt = now;

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
