using System.Security.Claims;
using System.Text;
using _1Rad.Domain.Entities;
using _1Rad.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace _1RadAPI.Middleware;

// Phase B2 Track 2 — server-side dedupe for the offline outbox.
//
// The flow:
//   1. Inspect the inbound request. Skip non-mutating methods (GET / HEAD /
//      OPTIONS), skip when the Idempotency-Key header is missing.
//   2. Look up (Key, UserId). If a non-expired record exists for the SAME
//      method+path, replay the stored response (status + body + content-
//      type). The action handler is never invoked — the retry is a true
//      no-op.
//   3. Otherwise, swap the response body stream for a MemoryStream, let the
//      pipeline run, and on a 2xx success persist (key, method, path,
//      status, body) with a 24h TTL.
//
// Why per-user composite key: two clients can independently generate the
// same UUID (it's improbable but not impossible, and we don't fully trust
// client-side randomness on shared kiosk laptops). Including UserId
// guarantees that one user's idempotency record can never short-circuit
// another user's request.
//
// Why NOT cache failures (4xx/5xx): a 404 today might be a 201 tomorrow
// (the user fixed the referenced resource). Caching it would lock the
// client into a stale failure for 24h. Successes only.
//
// Body capture: ASP.NET Core writes responses to HttpContext.Response.Body
// directly. We MemoryStream-swap so we can both forward bytes to the
// real client and persist them. The cost is one extra copy per mutating
// request — negligible for the small JSON payloads this API serves.
public class IdempotencyMiddleware
{
    private static readonly TimeSpan TTL = TimeSpan.FromHours(24);
    // Hard cap on body size we'll cache. A clinical mutation that returns
    // more than 64KB is unusual; capping protects the dedupe table from
    // bloating if someone wires up a large file upload through here.
    private const int MaxCachedBodyBytes = 64 * 1024;

    private readonly RequestDelegate _next;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    public IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ApplicationDbContext db)
    {
        var method = context.Request.Method;
        // Only mutating verbs need dedupe. A retried GET is harmless server-
        // side; skipping them keeps the dedupe table small.
        if (method == HttpMethods.Get || method == HttpMethods.Head || method == HttpMethods.Options)
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("Idempotency-Key", out var keyValues))
        {
            await _next(context);
            return;
        }
        var key = keyValues.ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 80)
        {
            // Header present but unusable — fall through. We don't 400 on
            // this because rejecting could break a client mid-recovery.
            await _next(context);
            return;
        }

        var userId = ExtractUserId(context);
        var path   = context.Request.Path.Value ?? string.Empty;

        // --- Lookup ---
        // Resilience: if the IdempotencyKeys table is missing (schema script
        // not yet applied) or the DB hiccups, we must NOT 500 the underlying
        // mutation. Degrade to "no dedupe" — the request still goes through;
        // we just lose the retry-replay guarantee for this one call.
        IdempotencyRecord existing;
        try
        {
            existing = await db.IdempotencyKeys
                .AsNoTracking()
                .Where(r => r.Key == key && r.UserId == userId)
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Idempotency lookup failed (is the IdempotencyKeys table applied?) — proceeding WITHOUT dedupe for key {Key}", key);
            await _next(context);
            return;
        }

        if (existing != null)
        {
            if (existing.ExpiresAt <= DateTime.UtcNow)
            {
                // Expired — clean up opportunistically and execute fresh.
                try
                {
                    await db.IdempotencyKeys
                        .Where(r => r.Key == key && r.UserId == userId)
                        .ExecuteDeleteAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to purge expired idempotency record {Key}", key);
                }
            }
            else if (existing.Method != method || existing.Path != path)
            {
                // Same key reused against a different endpoint — almost
                // certainly a client bug. Return 422 so the client doesn't
                // assume the original request succeeded. (RFC draft uses 422
                // for "key reused with mismatched request.")
                context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    "{\"error\":\"Idempotency-Key was already used for a different request.\"}");
                return;
            }
            else
            {
                // Hit — replay.
                context.Response.StatusCode = existing.ResponseStatus;
                if (!string.IsNullOrEmpty(existing.ResponseContentType))
                {
                    context.Response.ContentType = existing.ResponseContentType;
                }
                if (!string.IsNullOrEmpty(existing.ResponseBody))
                {
                    await context.Response.WriteAsync(existing.ResponseBody);
                }
                context.Response.Headers["X-Idempotent-Replay"] = "true";
                return;
            }
        }

        // --- Capture ---
        var originalBodyStream = context.Response.Body;
        using var memStream = new MemoryStream();
        context.Response.Body = memStream;

        try
        {
            await _next(context);
        }
        catch
        {
            // Restore the real stream before rethrowing so the exception
            // handler middleware can write its 500 body to the client.
            context.Response.Body = originalBodyStream;
            throw;
        }

        // Read what the action wrote, send it to the real client, optionally persist.
        memStream.Position = 0;
        var captured = memStream.ToArray();
        await originalBodyStream.WriteAsync(captured.AsMemory(0, captured.Length));
        context.Response.Body = originalBodyStream;

        var status = context.Response.StatusCode;
        if (status >= 200 && status < 300 && captured.Length <= MaxCachedBodyBytes)
        {
            try
            {
                var record = new IdempotencyRecord
                {
                    Key                 = key,
                    UserId              = userId,
                    Method              = method,
                    Path                = path,
                    ResponseStatus      = status,
                    ResponseBody        = Encoding.UTF8.GetString(captured),
                    ResponseContentType = context.Response.ContentType,
                    CreatedAt           = DateTime.UtcNow,
                    ExpiresAt           = DateTime.UtcNow.Add(TTL),
                };
                db.IdempotencyKeys.Add(record);
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Race: another concurrent retry inserted the same key first.
                // That's the WHOLE POINT of the dedupe table — we just lost
                // a race that the row we'd have written would have served
                // anyway. Swallow.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist idempotency record {Key}", key);
            }
        }
    }

    private static Guid ExtractUserId(HttpContext context)
    {
        // Returns IdempotencyRecord.AnonymousUserId (Guid.Empty) when no
        // authenticated principal — the table stores the sentinel rather
        // than NULL because SQL Server forbids NULL columns in the PK.
        var user = context.User;
        if (user?.Identity?.IsAuthenticated != true) return IdempotencyRecord.AnonymousUserId;
        var raw = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
               ?? user.FindFirst("sub")?.Value;
        return Guid.TryParse(raw, out var id) ? id : IdempotencyRecord.AnonymousUserId;
    }
}
