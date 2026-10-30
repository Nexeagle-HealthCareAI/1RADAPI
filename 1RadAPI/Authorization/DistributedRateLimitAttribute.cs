using System.Reflection;
using _1Rad.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace _1RadAPI.Authorization;

/// <summary>
/// Fixed-window rate limit counted in the database, so every API instance shares one
/// count (gap analysis P3-9). Replaces the in-memory ASP.NET limiter, which counted per
/// process and had one global OTP bucket for the whole platform.
///
/// The bucket key is <c>Name</c> plus, optionally, a route value (<c>Route</c>) and/or a
/// property of the request body (<c>BodyProperty</c>, e.g. "Mobile" or "Identifier",
/// normalised to lower-case without spaces). Stack several attributes for several limits
/// (e.g. per-mobile and a platform-wide ceiling). Over the limit → 429 with Retry-After.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class DistributedRateLimitAttribute : Attribute, IAsyncActionFilter
{
    public DistributedRateLimitAttribute(string name, int limit, int windowSeconds)
    {
        Name = name;
        Limit = limit;
        WindowSeconds = windowSeconds;
    }

    public string Name { get; }
    public int Limit { get; }
    public int WindowSeconds { get; }
    public string? Route { get; set; }
    public string? BodyProperty { get; set; }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var parts = new List<string> { Name };

        if (Route != null)
            parts.Add(context.RouteData.Values.TryGetValue(Route, out var rv) ? rv?.ToString() ?? "" : "");

        if (BodyProperty != null)
        {
            string? value = null;
            foreach (var arg in context.ActionArguments.Values)
            {
                if (arg == null) continue;
                var prop = arg.GetType().GetProperty(BodyProperty, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null) continue;
                value = prop.GetValue(arg)?.ToString();
                break;
            }
            parts.Add(new string((value ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant());
        }

        var key = string.Join(":", parts);
        var limiter = context.HttpContext.RequestServices.GetRequiredService<IDistributedRateLimiter>();
        var (allowed, retryAfter) = await limiter.TryAcquireAsync(key, Limit, TimeSpan.FromSeconds(WindowSeconds), context.HttpContext.RequestAborted);

        if (!allowed)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            context.HttpContext.Response.Headers["Retry-After"] = seconds.ToString();
            context.Result = new ObjectResult(new
            {
                success = false,
                error = "Too many attempts. Please wait and try again.",
                errorCode = "RATE_LIMITED",
                retryAfterSeconds = seconds,
            })
            { StatusCode = StatusCodes.Status429TooManyRequests };
            return;
        }

        await next();
    }
}
