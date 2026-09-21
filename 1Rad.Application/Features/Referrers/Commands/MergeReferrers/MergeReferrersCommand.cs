using System;
using _1Rad.Domain.Exceptions;
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
            throw new ValidationException("Cannot merge a partner into itself.");

        var source = await _context.Referrers
            .FirstOrDefaultAsync(r => r.ReferrerId == request.SourceReferrerId && r.HospitalId == hospitalId, ct);
            
        if (source == null || source.DeletedAt != null)
            throw new NotFoundException("Duplicate partner not found or is already deleted.");

        var target = await _context.Referrers
            .FirstOrDefaultAsync(r => r.ReferrerId == request.TargetReferrerId && r.HospitalId == hospitalId, ct);
            
        if (target == null || target.DeletedAt != null)
            throw new NotFoundException("Primary partner not found or is deleted.");

        // Self / walk-in is a bucket, not a partner: folding a real partner into it
        // would erase their payouts from every partner report, and folding "Self"
        // into a partner would credit walk-in visits to them.
        if (NameNormalizer.SameName(source.Name, "Self") || NameNormalizer.SameName(target.Name, "Self"))
            throw new ValidationException("Self / walk-in cannot be merged with a partner.");

        // A partner that is already an alias of someone else has to be unmerged
        // first — silently re-parenting it would orphan the earlier merge.
        if (source.MergedIntoId.HasValue)
            throw new ValidationException("This partner is already merged into another partner. Unmerge it first.");

        // Merge into the target's ROOT, not the target itself, so a chain
        // (A → B → C) never forms; and refuse a merge that would close a loop
        // (merging B into A when A is already an alias of B).
        var rootId = target.ReferrerId;
        var seen = new HashSet<Guid> { rootId };
        var cursor = target;
        while (cursor.MergedIntoId.HasValue)
        {
            var nextId = cursor.MergedIntoId.Value;
            if (!seen.Add(nextId)) break; // pre-existing cycle in the data — stop walking
            var next = await _context.Referrers
                .FirstOrDefaultAsync(r => r.ReferrerId == nextId && r.HospitalId == hospitalId, ct);
            if (next == null) break;
            cursor = next;
            rootId = next.ReferrerId;
        }
        if (rootId == source.ReferrerId)
            throw new ValidationException("That merge would create a loop: the primary partner is already an alias of this one.");

        // Virtual Merge
        source.MergedIntoId = rootId;
        source.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);

        return source.ReferrerId;
    }
}
