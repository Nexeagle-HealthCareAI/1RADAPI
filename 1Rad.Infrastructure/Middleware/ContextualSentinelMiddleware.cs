using System.Net;
using System.Security.Claims;
using _1Rad.Application.Interfaces;
using Microsoft.AspNetCore.Http;
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

    public async Task InvokeAsync(HttpContext context, IUserContext userContext)
    {
        // Skip validation for public endpoints or endpoints explicitly allowing anonymous access
        var endpoint = context.GetEndpoint();
        if (endpoint != null && 
            (endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() != null || 
             !endpoint.Metadata.Any(m => m is Microsoft.AspNetCore.Authorization.IAuthorizeData)))
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
        }

        await _next(context);
    }
}
