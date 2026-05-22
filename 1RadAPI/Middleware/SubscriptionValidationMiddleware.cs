using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace _1RadAPI.Middleware;

public class SubscriptionValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SubscriptionValidationMiddleware> _logger;

    public SubscriptionValidationMiddleware(RequestDelegate next, ILogger<SubscriptionValidationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLower() ?? string.Empty;

        // Skip static files, auth, and subscription status endpoints
        if (path.StartsWith("/api/v1/auth") ||
            path.StartsWith("/api/v1/subscriptions") ||
            !path.StartsWith("/api/v1/"))
        {
            await _next(context);
            return;
        }

        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata?.GetMetadata<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>() != null)
        {
            await _next(context);
            return;
        }

        // Only enforce for authenticated requests
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var hospitalIdClaim = context.User.FindFirst("HospitalId")?.Value;
            if (Guid.TryParse(hospitalIdClaim, out Guid hospitalId))
            {
                // We need to resolve DbContext from the request services because it's scoped
                var dbContext = context.RequestServices.GetRequiredService<IApplicationDbContext>();

                var currentSubscription = await dbContext.HospitalSubscriptions
                    .Where(s => s.HospitalId == hospitalId)
                    .OrderByDescending(s => s.EndDate)
                    .FirstOrDefaultAsync();

                bool isActive = currentSubscription != null &&
                                currentSubscription.Status != "Locked" &&
                                currentSubscription.EndDate.AddDays(2) >= DateTime.UtcNow;

                if (!isActive)
                {
                    _logger.LogWarning("Access denied for HospitalId {HospitalId} due to expired subscription and grace period.", hospitalId);
                    
                    context.Response.StatusCode = 402; // Payment Required
                    context.Response.ContentType = "application/json";
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
        }

        await _next(context);
    }
}
