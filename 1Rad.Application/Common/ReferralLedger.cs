using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Common;

/// <summary>
/// Re-walks a referrer's live commission history to keep
/// <see cref="_1Rad.Domain.Entities.ReferralCommission.AccumulatedTotal"/>
/// honest — before this existed, the identical "load every live commission
/// for this referrer, oldest first, re-stamp a running sum" loop was
/// independently copy-pasted across ten call sites (2026-07-24 architecture
/// audit), any one of which could silently drift from the others.
/// </summary>
public static class ReferralLedger
{
    /// <summary>
    /// Call after your own commission edits have been applied to tracked
    /// entities (new row added, amount changed, etc.) — this re-queries the
    /// referrer's live rows itself, so already-tracked in-memory changes are
    /// picked up, and re-stamps every row's AccumulatedTotal as a running sum
    /// ordered by TransactionDate. Does not call SaveChanges — the caller's
    /// existing save (often already needed for other changes in the same
    /// request) covers this too.
    /// </summary>
    public static async Task RecomputeAccumulatedTotal(IApplicationDbContext context, Guid referrerId, Guid hospitalId, CancellationToken cancellationToken)
    {
        var rows = await context.ReferralCommissions
            .Where(c => c.ReferrerId == referrerId && c.HospitalId == hospitalId && c.DeletedAt == null)
            .OrderBy(c => c.TransactionDate)
            .ToListAsync(cancellationToken);

        decimal running = 0;
        foreach (var c in rows)
        {
            running += c.CommissionAmount;
            c.AccumulatedTotal = running;
        }
    }
}
