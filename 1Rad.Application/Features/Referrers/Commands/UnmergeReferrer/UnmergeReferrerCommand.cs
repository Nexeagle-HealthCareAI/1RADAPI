using System;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.UnmergeReferrer;

public record UnmergeReferrerCommand(Guid SourceReferrerId) : IRequest<Guid>;

public class UnmergeReferrerCommandHandler : IRequestHandler<UnmergeReferrerCommand, Guid>
{
    private readonly IApplicationDbContext _context;

    public UnmergeReferrerCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<Guid> Handle(UnmergeReferrerCommand request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty)
            throw new UnauthorizedAccessException("Hospital context required.");

        var source = await _context.Referrers
            .FirstOrDefaultAsync(r => r.ReferrerId == request.SourceReferrerId && r.HospitalId == hospitalId, ct);
            
        if (source == null || source.DeletedAt != null)
            throw new ArgumentException("Duplicate partner not found or is deleted.");

        if (source.MergedIntoId == null)
            throw new ArgumentException("This partner is not currently merged into another.");

        // Revert Virtual Merge
        source.MergedIntoId = null;
        source.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

        return source.ReferrerId;
    }
}
