using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Common;

/// <summary>
/// Finds the partner record a visit's referrer NAME refers to, so the visit can carry the
/// partner's id (<see cref="_1Rad.Domain.Entities.Appointment.ReferrerId"/>). Used where a
/// write path only has the text - an edited appointment, an arrival that predates the id.
/// It never creates a partner; that stays with the booking / reassign flows.
/// </summary>
internal static class ReferrerLookup
{
    /// <summary>
    /// The id of this centre's partner with that name (case-insensitive), or null when the name
    /// is blank, is the "Self" walk-in bucket, or matches no partner. A live record is preferred
    /// over a deleted one so a re-created partner wins over its tombstone.
    /// </summary>
    public static async Task<Guid?> FindIdByNameAsync(IApplicationDbContext ctx, Guid hospitalId, string? name, CancellationToken ct)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0 || NameNormalizer.SameName(trimmed, "Self")) return null;

        var lower = trimmed.ToLower();
        var id = await ctx.Referrers
            .Where(r => r.HospitalId == hospitalId && r.Name.ToLower() == lower)
            .OrderBy(r => r.DeletedAt == null ? 0 : 1)
            .ThenBy(r => r.ReferrerId)
            .Select(r => (Guid?)r.ReferrerId)
            .FirstOrDefaultAsync(ct);
        return id;
    }
}
