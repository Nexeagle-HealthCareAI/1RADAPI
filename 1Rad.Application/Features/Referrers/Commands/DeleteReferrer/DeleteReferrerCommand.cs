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

        // Soft delete so the sync engine propagates the tombstone and historic
        // commissions keep their referrer reference for reporting.
        var now = DateTime.UtcNow;
        referrer.DeletedAt = now;
        referrer.UpdatedAt = now;

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
