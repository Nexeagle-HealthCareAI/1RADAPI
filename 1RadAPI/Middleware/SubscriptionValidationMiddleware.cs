using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace _1RadAPI.Middleware;

// Server-side subscription lock (gap analysis P1-1).
//
// Before Oct 2026 this middleware read a "HospitalId" claim that the JWT never
// carries (the centre is in "cid"), so it never fired and a locked centre kept full
// API access — only the web UI's lock screen stopped it.
//
// The lock rule mirrors GetSubscriptionStatusQuery exactly so the API and the UI
// agree: the centre's latest subscription row (by CreatedAt) is locked when
// IsLocked is set, or when it is more than 2 days past EndDate. A centre with NO
// subscription row is not locked (pre-subscription behaviour).
//
// Always allowed while locked: auth, subscriptions (status / plans / payment
// request — the way out), the user's own sessions, system clock and health.
public class SubscriptionValidationMiddleware
{
    private static readonly string[] AlwaysAllowedPrefixes =
    {
        "/api/v1/auth",
        "/api/v1/subscriptions",
        "/api/v1/me/",
        "/api/v1/system",
        "/api/v1/health",
    };

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly RequestDelegate _next;
    private readonly ILogger<SubscriptionValidationMiddleware> _logger;
    private readonly IMemoryCache _cache;

    public SubscriptionValidationMiddleware(RequestDelegate next, ILogger<SubscriptionValidationMiddleware> logger, IMemoryCache cache)
    {
        _next = next;
        _logger = logger;
        _cache = cache;
    }

    public static string CacheKey(Guid hospitalId) => $"sub-lock:{hospitalId}";

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;

        if (!path.StartsWith("/api/v1/") || AlwaysAllowedPrefixes.Any(p => path.StartsWith(p)))
        {
            await _next(context);
            return;
        }

        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata?.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() != null)
        {
            await _next(context);
            return;
        }

        // Only access tokens carry a centre; initiation / reset tokens pass through.
        var tokenType = context.User.FindFirst("type")?.Value;
        if (context.User.Identity?.IsAuthenticated == true &&
            (tokenType == null || tokenType == "access") &&
            Guid.TryParse(context.User.FindFirst("cid")?.Value, out var hospitalId) &&
            hospitalId != Guid.Empty)
        {
            var locked = await _cache.GetOrCreateAsync(CacheKey(hospitalId), async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheTtl;
                var db = context.RequestServices.GetRequiredService<IApplicationDbContext>();
                var sub = await db.HospitalSubscriptions
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(s => s.HospitalId == hospitalId)
                    .OrderByDescending(s => s.CreatedAt)
                    .Select(s => new { s.IsLocked, s.EndDate })
                    .FirstOrDefaultAsync();

                return sub != null && (sub.IsLocked || DateTime.UtcNow > sub.EndDate.AddDays(2));
            });

            if (locked)
            {
                _logger.LogWarning("Subscription locked: request {Path} refused for HospitalId {HospitalId}.", path, hospitalId);
                context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    error = "Subscription has expired. Payment is required to continue using the system.",
                    errorCode = "PAYMENT_REQUIRED",
                    isLocked = true
                });
                return;
            }
        }

        await _next(context);
    }
}
