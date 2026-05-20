using System.Net;
using System.Security.Claims;
using _1Rad.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Middleware;

public class ContextualSentinelMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ContextualSentinelMiddleware> _logger;

    public ContextualSentinelMiddleware(RequestDelegate next, ILogger<ContextualSentinelMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IUserContext userContext, IApplicationDbContext db)
    {
        // Skip validation for public endpoints or endpoints explicitly allowing anonymous access
        var endpoint = context.GetEndpoint();
        if (endpoint != null &&
            (endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null ||
             !endpoint.Metadata.Any(m => m is IAuthorizeData)))
        {
            await _next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            // Tactical: Initiation and PasswordReset tokens do not have hospital context
            var tokenType = context.User.FindFirstValue("type");
            if (tokenType != null && tokenType != "access")
            {
                await _next(context);
                return;
            }

            var userId = userContext.UserId;
            var currentHospitalId = userContext.HospitalId;
            var authorizedHubs = userContext.AuthorizedHospitalIds.ToList();

            // Tactical Check: Ensure the requested context is within authorized Hubs
            if (currentHospitalId == Guid.Empty)
            {
                _logger.LogWarning("Tactical Breach: Authenticated request from User {UserId} missing 'cid' (Hospital context).", userId);
                context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "Institutional context missing. Please select a hospital." });
                return;
            }

            if (!authorizedHubs.Contains(currentHospitalId))
            {
                _logger.LogWarning("Tactical Breach: User {UserId} attempted access to unauthorized Hospital context {cid}.", userId, currentHospitalId);
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Access Denied: You are not authorized for this institutional context." });
                return;
            }

            // Context is validated and ready for Global Query Filters
            _logger.LogDebug("Context Verified: User {UserId} | Hospital {cid}", userId, currentHospitalId);

            // --- Custom Role Permission Gate ---
            // For endpoints with [Authorize(Roles=...)]:
            //   1. Check if the user already has a matching system role in their JWT — if yes, pass through normally.
            //   2. If NOT, query the DB for a custom role (hospital-scoped) with a RoutePath matching this request.
            //      - Match found  → inject the required role claim into the ClaimsPrincipal so ASP.NET's
            //                       [Authorize(Roles=...)] passes naturally downstream.
            //      - No match     → return 403 here before the pipeline continues.
            var roleRestrictedAttributes = endpoint?.Metadata
                .OfType<AuthorizeAttribute>()
                .Where(a => !string.IsNullOrEmpty(a.Roles))
                .ToList();

            if (roleRestrictedAttributes?.Count > 0)
            {
                var userSystemRoles = context.User
                    .FindAll(ClaimTypes.Role)
                    .Select(c => c.Value)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var requiredRoles = roleRestrictedAttributes
                    .SelectMany(a => a.Roles!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Only hit the DB if the user lacks a matching system role
                if (!userSystemRoles.Overlaps(requiredRoles))
                {
                    var requestPath = context.Request.Path.Value?.TrimStart('/').ToLowerInvariant() ?? string.Empty;

                    var hasCustomPermission = await db.UserHospitalMappings
                        .Where(m => m.UserId == userId && m.HospitalId == currentHospitalId)
                        .SelectMany(m => m.CustomRoles)
                        .SelectMany(cr => cr.Permissions)
                        .AnyAsync(p => requestPath.StartsWith(
                            p.RoutePath.TrimStart('/').ToLowerInvariant()));

                    if (!hasCustomPermission)
                    {
                        // Neither system role nor custom role grants access — deny
                        _logger.LogWarning(
                            "Authorization Denied: User {UserId} at Hospital {HospitalId} lacks both system role and custom role permission for {Path}.",
                            userId, currentHospitalId, requestPath);
                        context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            success = false,
                            error = "Access Denied: You do not have the required role or custom permission for this action."
                        });
                        return;
                    }

                    // Custom role matches — inject one of the required role claims so that
                    // ASP.NET's [Authorize(Roles=...)] evaluation passes downstream.
                    var grantRole = requiredRoles.First();
                    var identity = context.User.Identity as ClaimsIdentity;
                    identity?.AddClaim(new Claim(ClaimTypes.Role, grantRole));

                    _logger.LogDebug(
                        "Custom Role Override: User {UserId} granted access to {Path} via custom role permission (injected role '{Role}').",
                        userId, requestPath, grantRole);
                }
            }
        }

        await _next(context);
    }
}
