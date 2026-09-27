using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.WriteOffReferralDeficit;

/// <summary>
/// The centre absorbs a partner's outstanding clawback/reversal deficit (negative
/// UNPAID rows) instead of recovering it from future payouts.
///
/// It used to be a browser-computed "WRITE-OFF" line booked through the batch
/// endpoint: a compensating +D PAID row was added but the negative rows stayed
/// open — so once payouts could NET deficits, a written-off deficit would have
/// been recovered a second time. The server now does the whole thing:
///   • the open negative rows are settled (cancelled — "written off"), so they no
///     longer count anywhere and can never be netted;
///   • a +D PAID "WRITE-OFF" row is booked so the cash the centre actually paid
///     out (and will not get back) still shows in cash-basis figures;
///   • the amount is computed here from live rows, and the call is naturally
///     idempotent — a second call finds nothing open and is refused.
/// </summary>
public record WriteOffReferralDeficitCommand(Guid ReferrerId) : IRequest<WriteOffReferralDeficitResult>;

public record WriteOffReferralDeficitResult(decimal WrittenOff, int RowsSettled);

public class WriteOffReferralDeficitCommandHandler : IRequestHandler<WriteOffReferralDeficitCommand, WriteOffReferralDeficitResult>
{
    private readonly IApplicationDbContext _context;

    public WriteOffReferralDeficitCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<WriteOffReferralDeficitResult> Handle(WriteOffReferralDeficitCommand request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty)
            throw new UnauthorizedAccessException("Hospital context required.");

        var registry = await _context.Referrers
            .Where(r => r.HospitalId == hospitalId)
            .Select(r => new { r.ReferrerId, r.MergedIntoId, r.Name })
            .ToListAsync(ct);
        var byId = registry.ToDictionary(r => r.ReferrerId);
        if (!byId.ContainsKey(request.ReferrerId))
            throw new NotFoundException($"Referrer [{request.ReferrerId}] was not found for this facility.");

        Guid Root(Guid id)
        {
            var seen = new HashSet<Guid>();
            while (byId.TryGetValue(id, out var n) && n.MergedIntoId.HasValue && seen.Add(id)) id = n.MergedIntoId.Value;
            return id;
        }
        var root = Root(request.ReferrerId);
        var aliasIds = registry.Where(r => Root(r.ReferrerId) == root).Select(r => r.ReferrerId).ToList();

        var open = await _context.ReferralCommissions
            .Where(c => c.HospitalId == hospitalId && c.DeletedAt == null && c.CommissionAmount < 0m
                        && aliasIds.Contains(c.ReferrerId)
                        && c.Status != CommissionStatus.Paid
                        && c.Status != CommissionStatus.Cancelled && c.Status != "CANCELLED")
            .ToListAsync(ct);
        if (open.Count == 0)
            throw new BusinessRuleViolationException("Nothing to write off — this partner has no outstanding deficit.");

        var now = DateTime.UtcNow;
        var actor = await CommissionActor.ResolveAsync(_context, ct);
        var deficit = open.Sum(c => -c.CommissionAmount);
        var rootName = byId[root].Name ?? "Unknown";

        foreach (var c in open)
        {
            c.Status = CommissionStatus.Cancelled;
            c.UpdatedBy = actor;
            c.Remarks = (c.Remarks ?? string.Empty) + $" [Written off {now:yyyy-MM-dd} — centre absorbed the deficit]";
            c.UpdatedAt = now;
        }

        _context.ReferralCommissions.Add(new ReferralCommission
        {
            HospitalId = hospitalId,
            ReferrerId = root,
            ReferrerName = rootName,
            Modality = "WRITE-OFF",
            CommissionAmount = deficit,
            Status = CommissionStatus.Paid,
            PaymentDate = now,
            PayeeName = "CENTRE ABSORBED",
            CreatedBy = actor,
            UpdatedBy = actor,
            TransactionDate = now,
            ServiceDate = now,
            Remarks = $"DEFICIT WRITE-OFF (₹{deficit:0.##}) — centre absorbed",
            UpdatedAt = now,
        });

        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A payout netted (or another write-off cancelled) one of these rows since we read them.
            throw new ConflictException("This partner's deficit was just changed by someone else (a payout or another write-off). Refresh and check what is still outstanding.");
        }

        await ReferralLedger.RecomputeAccumulatedTotal(_context, root, hospitalId, ct);
        foreach (var id in open.Select(c => c.ReferrerId).Distinct().Where(id => id != root))
            await ReferralLedger.RecomputeAccumulatedTotal(_context, id, hospitalId, ct);
        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The write-off is committed; the running total is derived and re-stamped on the next change.
        }

        return new WriteOffReferralDeficitResult(deficit, open.Count);
    }
}
