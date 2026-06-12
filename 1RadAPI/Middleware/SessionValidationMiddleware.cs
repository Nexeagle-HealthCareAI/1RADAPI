using System.Security.Claims;
using _1Rad.Application.Interfaces;
using _1Rad.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace _1RadAPI.Middleware;

// Stateful JWT check. Runs after [Authorize] has already validated the token
// signature + lifetime, so by the time we get here the principal is real and
// we just need to confirm the session id is still alive on the server.
//
// Three cases:
//   1. No `sid` claim → legacy token from before migration 46. We reject
//      it; the frontend handles 401 by forcing a fresh login that issues a
//      sid-bearing token. One-time hit per user, then it's done.
//   2. `sid` is present and active (cache hit) → pass through, throttled
//      LastSeenAt write so the Active Sessions UI has fresh data.
//   3. `sid` is present and active (cache miss) → look up in DB. If active,
//      prime the cache. If not, reject as revoked.
//
// The middleware writes back a structured 401 body that the frontend can
// distinguish from an ordinary auth failure (so it can show the "signed in
// elsewhere" banner instead of the generic invalid-login message).
public class SessionValidationMiddleware
{
    private readonly RequestDelegate _next;

    // Throttle the LastSeenAt update so a busy worklist (one poll every 5s)
    // doesn't slam the DB with 12 writes a minute per user. 30s is far below
    // the idle timeout window, so the staleness is invisible.
    private const int LastSeenWriteThrottleSeconds = 30;
    private static readonly Dictionary<Guid, DateTime> _lastSeenWrites = new();
    private static readonly object _lastSeenLock = new();

    public SessionValidationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IActiveSessionCache cache,
        ApplicationDbContext db)
    {
        // Bail on unauthenticated calls — they either need no token (public
        // endpoint) or [Authorize] will short-circuit elsewhere with 401.
        var user = context.User;
        if (user?.Identity == null || !user.Identity.IsAuthenticated)
        {
            await _next(context);
            return;
        }

        // Registration-stage (initiation) tokens are pre-session BY DESIGN:
        // they're minted at OTP-verify before any RefreshTokens row exists,
        // live 15 minutes, and are only accepted by the two InitiationOnly
        // registration endpoints. Without this exemption the sid gate 401s
        // the whole signup flow with MISSING_SID (the frontend reads that as
        // "session-upgraded" and kicks the new user off the form). Scoped to
        // BOTH the claim and the two endpoints so an initiation token still
        // can't slip past session checks anywhere else.
        var path = context.Request.Path.Value ?? string.Empty;
        if (user.FindFirst("type")?.Value == "initiation"
            && (path.EndsWith("/auth/identity-setup", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/auth/deploy-infrastructure", StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        var sidString = user.FindFirst(JwtRegisteredClaimNames.Sid)?.Value
                        ?? user.FindFirst(ClaimTypes.Sid)?.Value;
        if (!Guid.TryParse(sidString, out var sessionId))
        {
            await WriteRevokedAsync(context, "MISSING_SID");
            return;
        }

        if (!cache.IsActive(sessionId))
        {
            // Cache miss could mean revoked, never-cached, or process restart.
            // The DB is canonical. One lookup keyed off the filtered index.
            var nowUtc = DateTime.UtcNow;
            var dbRow = await db.RefreshTokens
                .AsNoTracking()
                .Where(rt => rt.SessionId == sessionId
                          && rt.RevokedAt == null
                          && rt.ExpiresAt > nowUtc)
                .Select(rt => new { rt.ExpiresAt })
                .FirstOrDefaultAsync();
            if (dbRow == null)
            {
                await WriteRevokedAsync(context, "SESSION_REVOKED");
                return;
            }
            cache.MarkActive(sessionId, dbRow.ExpiresAt);
        }

        // Throttled LastSeenAt update. We do this AFTER passing the gate so a
        // revoked session can't keep refreshing its own activity timestamp.
        if (ShouldWriteLastSeen(sessionId))
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE dbo.RefreshTokens SET LastSeenAt = SYSUTCDATETIME() WHERE SessionId = {0} AND RevokedAt IS NULL",
                    sessionId);
            }
            catch
            {
                // LastSeenAt is best-effort. If the write fails the user's
                // session still works — only the Active Sessions UI's
                // "last seen X min ago" gets slightly stale.
            }
        }

        await _next(context);
    }

    private static bool ShouldWriteLastSeen(Guid sessionId)
    {
        var nowUtc = DateTime.UtcNow;
        lock (_lastSeenLock)
        {
            if (_lastSeenWrites.TryGetValue(sessionId, out var last)
                && (nowUtc - last).TotalSeconds < LastSeenWriteThrottleSeconds)
            {
                return false;
            }
            _lastSeenWrites[sessionId] = nowUtc;
            // Prevent unbounded growth — only an issue for very-long-running
            // processes with many distinct users. Drop the oldest 25% when
            // the map crosses 10k entries.
            if (_lastSeenWrites.Count > 10_000)
            {
                var toDrop = _lastSeenWrites
                    .OrderBy(kv => kv.Value)
                    .Take(2_500)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var k in toDrop) _lastSeenWrites.Remove(k);
            }
            return true;
        }
    }

    private static async Task WriteRevokedAsync(HttpContext context, string code)
    {
        // Same status code as ordinary auth failure (401) so the [Authorize]
        // pipeline's downstream handling stays consistent. The `code` field
        // is what the frontend reads to choose between "your credentials are
        // bad" and "you've been signed out elsewhere".
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            $"{{\"success\":false,\"code\":\"{code}\",\"error\":\"Your session has ended. Please sign in again.\"}}");
    }
}
