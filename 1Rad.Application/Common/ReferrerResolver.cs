using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Common;

/// <summary>
/// The one rule for "which partner does this visit credit?" (review #13, #25).
/// Booking, visit edits and the commission reconcile all use it, so a partner the
/// front desk PICKED is the one that gets paid, and two different people who share
/// a name are never folded into one payee.
///
/// Order:
///  1. The partner id the screen sent — must be this centre's, not deleted; a merged
///     duplicate resolves to its primary partner.
///  2. The exact stored name (upper-case) among live partners that aren't merged away.
///  3. The same name ignoring titles ("Dr", "Md" ...), only when exactly one live,
///     unmerged partner has it — or when the mobile number picks one of several.
///  4. The mobile number, only when exactly one live, unmerged partner has it AND
///     the surname matches (so "Dr R Kumar" and "Rajesh Kumar" on the same number
///     are one person, but a clinic's shared number never joins two doctors).
/// Returns null when nothing matches; the caller decides whether to create a partner.
/// </summary>
public static class ReferrerResolver
{
    public static async Task<Referrer?> ResolveAsync(
        IApplicationDbContext ctx, Guid hospitalId, Guid? referrerId, string? name, string? mobile, CancellationToken ct)
    {
        if (referrerId.HasValue && referrerId.Value != Guid.Empty)
        {
            var picked = await ByIdAsync(ctx, hospitalId, referrerId.Value, ct);
            if (picked != null) return picked;
        }

        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0 || NameNormalizer.SameName(trimmed, "Self")) return null;

        var upper = NameNormalizer.Upper(trimmed);
        var exact = await ctx.Referrers
            .Where(r => r.HospitalId == hospitalId && r.DeletedAt == null && r.Name == upper)
            .OrderBy(r => r.MergedIntoId == null ? 0 : 1)
            .FirstOrDefaultAsync(ct);
        if (exact != null)
            return exact.MergedIntoId.HasValue ? (await ByIdAsync(ctx, hospitalId, exact.ReferrerId, ct) ?? exact) : exact;

        var key = NameNormalizer.Normalize(trimmed);
        var digits = MobileDigits(mobile);

        // Name and mobile are compared in memory: the normaliser is C# only. Only the
        // three columns needed are read, so this stays light even for large centres.
        var live = await ctx.Referrers
            .Where(r => r.HospitalId == hospitalId && r.DeletedAt == null && r.MergedIntoId == null)
            .Select(r => new { r.ReferrerId, r.Name, r.Contact })
            .ToListAsync(ct);

        var sameName = live.Where(r => NameNormalizer.Normalize(r.Name) == key).ToList();
        Guid? chosen = null;
        if (sameName.Count == 1) chosen = sameName[0].ReferrerId;
        else if (sameName.Count > 1 && digits.Length == 10)
        {
            var byMobile = sameName.Where(r => MobileDigits(r.Contact) == digits).ToList();
            if (byMobile.Count == 1) chosen = byMobile[0].ReferrerId;
        }

        if (chosen == null && digits.Length == 10)
        {
            var onNumber = live.Where(r => MobileDigits(r.Contact) == digits).ToList();
            if (onNumber.Count == 1 && SameSurname(onNumber[0].Name, trimmed)) chosen = onNumber[0].ReferrerId;
        }

        if (chosen == null) return null;
        return await ctx.Referrers.FirstOrDefaultAsync(r => r.ReferrerId == chosen.Value && r.HospitalId == hospitalId, ct);
    }

    /// <summary>This centre's live partner with that id, following merges to the primary.</summary>
    public static async Task<Referrer?> ByIdAsync(IApplicationDbContext ctx, Guid hospitalId, Guid id, CancellationToken ct)
    {
        var current = await ctx.Referrers.FirstOrDefaultAsync(r => r.ReferrerId == id && r.HospitalId == hospitalId, ct);
        var seen = new HashSet<Guid>();
        while (current != null && current.MergedIntoId.HasValue && seen.Add(current.ReferrerId))
        {
            var nextId = current.MergedIntoId.Value;
            var next = await ctx.Referrers.FirstOrDefaultAsync(r => r.ReferrerId == nextId && r.HospitalId == hospitalId, ct);
            if (next == null) break;
            current = next;
        }
        return current is { DeletedAt: null } ? current : null;
    }

    /// <summary>A 10-digit Indian mobile from whatever was typed ("+91 98...", "098..."), or "".</summary>
    public static string MobileDigits(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var d = new string(raw.Where(char.IsDigit).ToArray());
        if (d.StartsWith("91") && d.Length == 12) d = d.Substring(2);
        else if (d.StartsWith("0") && d.Length == 11) d = d.Substring(1);
        return d;
    }

    /// <summary>True when the last word of both names (titles removed) is the same.</summary>
    public static bool SameSurname(string? a, string? b)
    {
        var ta = NameNormalizer.Normalize(a).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tb = NameNormalizer.Normalize(b).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return ta.Length > 0 && tb.Length > 0 && ta[^1] == tb[^1];
    }
}
