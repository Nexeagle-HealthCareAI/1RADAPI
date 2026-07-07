using System;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.MergeReferrers;

public record MergeReferrersCommand(Guid SourceReferrerId, Guid TargetReferrerId) : IRequest<Guid>;

public class MergeReferrersCommandHandler : IRequestHandler<MergeReferrersCommand, Guid>
{
    private readonly IApplicationDbContext _context;

    public MergeReferrersCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<Guid> Handle(MergeReferrersCommand request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty)
            throw new UnauthorizedAccessException("Hospital context required.");

        if (request.SourceReferrerId == request.TargetReferrerId)
            throw new ArgumentException("Cannot merge a partner into itself.");

        var source = await _context.Referrers
            .FirstOrDefaultAsync(r => r.ReferrerId == request.SourceReferrerId && r.HospitalId == hospitalId, ct);
            
        if (source == null || source.DeletedAt != null)
            throw new ArgumentException("Duplicate partner not found or is already deleted.");

        var target = await _context.Referrers
            .FirstOrDefaultAsync(r => r.ReferrerId == request.TargetReferrerId && r.HospitalId == hospitalId, ct);
            
        if (target == null || target.DeletedAt != null)
            throw new ArgumentException("Primary partner not found or is deleted.");

        // Virtual Merge
        source.MergedIntoId = target.ReferrerId;
        source.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

        return source.ReferrerId;
    }
}
