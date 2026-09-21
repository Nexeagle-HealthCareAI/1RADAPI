using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Common;

/// <summary>
/// Reads the current portal-link version of one or more partners. Query filters
/// are bypassed on purpose: the public portal has no hospital/user context, and
/// callers on the authenticated side have already scoped the referrer ids to the
/// caller's centre. A partner with no row is at version 0.
/// </summary>
public static class ReferralLinkVersions
{
    public static async Task<Dictionary<Guid, int>> GetAsync(
        IApplicationDbContext context, IReadOnlyCollection<Guid> referrerIds, CancellationToken ct)
    {
        if (referrerIds.Count == 0) return new Dictionary<Guid, int>();
        return await context.ReferrerLinkVersions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(v => referrerIds.Contains(v.ReferrerId))
            .ToDictionaryAsync(v => v.ReferrerId, v => v.Version, ct);
    }

    public static async Task<int> GetOneAsync(IApplicationDbContext context, Guid referrerId, CancellationToken ct)
    {
        var all = await GetAsync(context, new[] { referrerId }, ct);
        return all.TryGetValue(referrerId, out var v) ? v : 0;
    }
}

/// <summary>
/// The single check behind every public doctor-portal request: the token must be
/// well-formed, signed, unexpired, for THIS partner, and issued under the partner's
/// CURRENT link version — so a revoked link fails even though its signature and
/// expiry are still good.
/// </summary>
public enum ReferralLinkState
{
    Valid,
    /// <summary>Genuine, current-version link that simply ran out - the doctor may request a fresh one.</summary>
    Expired,
    /// <summary>Malformed, forged, for someone else, or revoked/replaced.</summary>
    Invalid,
}

public static class ReferralLinkAccess
{
    public static async Task<ReferralLinkState> CheckAsync(
        IApplicationDbContext context, IReferralLinkTokenService tokens, string? token, Guid referrerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return ReferralLinkState.Invalid;
        var version = await ReferralLinkVersions.GetOneAsync(context, referrerId, ct);
        if (tokens.Validate(token, referrerId, version)) return ReferralLinkState.Valid;

        // Not valid - but is it merely EXPIRED? (signed by us, this partner, current version)
        return tokens.TryReadClaims(token, out var claims)
               && claims.ReferrerId == referrerId
               && claims.Version == version
               && claims.IsExpired(DateTime.UtcNow)
            ? ReferralLinkState.Expired
            : ReferralLinkState.Invalid;
    }

    public static async Task<bool> IsValidAsync(
        IApplicationDbContext context, IReferralLinkTokenService tokens, string? token, Guid referrerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var version = await ReferralLinkVersions.GetOneAsync(context, referrerId, ct);
        return tokens.Validate(token, referrerId, version);
    }
}
