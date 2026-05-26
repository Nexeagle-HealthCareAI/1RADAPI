namespace _1RadAPI.Middleware;

/// <summary>
/// Stamps every request with a short correlation ID so error responses
/// returned to the client can be cross-referenced against server-side logs.
///
/// • Reuses an incoming X-Correlation-Id header if present (lets the
///   frontend / load-balancer set its own trace ID for distributed tracing).
/// • Otherwise generates a new short GUID-derived ID (no hyphens, first 12
///   chars) — short enough for a user to read aloud over phone support.
/// • Echoes the ID back as response header X-Correlation-Id AND stashes it
///   in HttpContext.Items["CorrelationId"] so the exception middleware can
///   include it in JSON error bodies.
/// </summary>
public class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "CorrelationId";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var id = context.Request.Headers.TryGetValue(HeaderName, out var inbound)
                 && !string.IsNullOrWhiteSpace(inbound.ToString())
            ? inbound.ToString()
            : Guid.NewGuid().ToString("N")[..12]; // 12-char short id

        context.Items[ItemKey] = id;

        // Add the header BEFORE the response starts (Azure & some proxies
        // forbid header mutations after first byte).
        context.Response.OnStarting(() =>
        {
            if (!context.Response.Headers.ContainsKey(HeaderName))
            {
                context.Response.Headers[HeaderName] = id;
            }
            return Task.CompletedTask;
        });

        await _next(context);
    }
}
