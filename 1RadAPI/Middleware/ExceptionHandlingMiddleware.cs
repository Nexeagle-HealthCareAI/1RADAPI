using System.Net;
using System.Text.Json;
using _1Rad.Domain.Exceptions;
// NOTE: do NOT add a `using FluentValidation;` here — both
// _1Rad.Domain.Exceptions and FluentValidation define a ValidationException,
// and the unqualified name becomes ambiguous. We reference FluentValidation's
// type by its full name below.

namespace _1RadAPI.Middleware;

/// <summary>
/// Global exception handler. Goals:
///   1. Never leak internal details (SQL, file paths, stack traces) to clients
///      in Production. Development gets the full message + stack.
///   2. Map known exception types to specific HTTP statuses + errorCodes so
///      the frontend can branch on the failure mode (auth invalid vs. DB
///      conflict vs. external service down).
///   3. Always include a correlation ID in the response body. Logs always
///      include the same ID, so engineering can grep one ID to find the full
///      trace.
///   4. Every uncaught exception ALWAYS logs the full stack with the
///      correlation ID — even when the client sees only a generic message.
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            var correlationId = context.Items[CorrelationIdMiddleware.ItemKey] as string ?? "n/a";
            _logger.LogError(
                ex,
                "[CorrelationId: {CorrelationId}] Unhandled exception on {Method} {Path}: {Message}",
                correlationId,
                context.Request.Method,
                context.Request.Path,
                ex.Message);

            await WriteErrorResponseAsync(context, ex, correlationId);
        }
    }

    private async Task WriteErrorResponseAsync(HttpContext context, Exception ex, string correlationId)
    {
        var (status, body) = MapException(ex, correlationId);

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = status;

        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = _env.IsDevelopment(),
        });
        await context.Response.WriteAsync(json);
    }

    /// <summary>
    /// Single source of truth for exception-to-HTTP mapping. Add new branches
    /// here when you introduce a new typed exception or want to surface a
    /// specific failure mode to the client.
    /// </summary>
    private (int Status, object Body) MapException(Exception ex, string correlationId)
    {
        var isDev = _env.IsDevelopment();

        switch (ex)
        {
            // ── Known domain exceptions (carry their own status + errorCode) ──
            case DomainException d:
                return (d.StatusCode, BuildBody(
                    success: false,
                    errorCode: d.ErrorCode,
                    message: d.Message,                        // safe — author chose it
                    correlationId: correlationId,
                    // Only echo additionalData in Development — handlers
                    // sometimes stash internal IDs / paths there.
                    additionalData: isDev ? d.AdditionalData : null,
                    stackTrace: isDev ? d.StackTrace : null));

            // ── Input validation (FluentValidation) ──
            case FluentValidation.ValidationException v:
                var errors = v.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
                return ((int)HttpStatusCode.BadRequest, BuildBody(
                    success: false,
                    errorCode: "VALIDATION_ERROR",
                    message: "One or more validation errors occurred.",
                    correlationId: correlationId,
                    extra: new { errors }));

            // ── Authorization ──
            case UnauthorizedAccessException:
                return ((int)HttpStatusCode.Forbidden, BuildBody(
                    success: false,
                    errorCode: "FORBIDDEN",
                    message: "You don't have permission to access this resource.",
                    correlationId: correlationId));

            // ── DB ──
            case Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException:
                return ((int)HttpStatusCode.Conflict, BuildBody(
                    success: false,
                    errorCode: "DB_CONCURRENCY_CONFLICT",
                    message: "The record was modified by someone else. Please reload and try again.",
                    correlationId: correlationId));

            case Microsoft.EntityFrameworkCore.DbUpdateException dbUpdate:
                return ((int)HttpStatusCode.BadRequest, BuildBody(
                    success: false,
                    errorCode: "DB_CONSTRAINT_ERROR",
                    message: ClassifyDbUpdateError(dbUpdate),
                    correlationId: correlationId,
                    stackTrace: isDev ? dbUpdate.StackTrace : null));

            // ── External / network failures ──
            case TimeoutException:
                return ((int)HttpStatusCode.GatewayTimeout, BuildBody(
                    success: false,
                    errorCode: "UPSTREAM_TIMEOUT",
                    message: "A dependent service timed out. Please retry shortly.",
                    correlationId: correlationId));

            case HttpRequestException httpEx:
                return ((int)HttpStatusCode.BadGateway, BuildBody(
                    success: false,
                    errorCode: "UPSTREAM_FAILURE",
                    message: "An external service is unreachable. Please retry shortly.",
                    correlationId: correlationId,
                    stackTrace: isDev ? httpEx.StackTrace : null));

            // ── Config / startup ──
            case InvalidOperationException io when io.Message.Contains("required but was not configured", StringComparison.OrdinalIgnoreCase):
                return ((int)HttpStatusCode.InternalServerError, BuildBody(
                    success: false,
                    errorCode: "CONFIG_MISSING",
                    message: "The service is misconfigured. Please contact support and quote the reference below.",
                    correlationId: correlationId,
                    stackTrace: isDev ? io.StackTrace : null));

            // ── JSON / model binding ──
            case JsonException:
                return ((int)HttpStatusCode.BadRequest, BuildBody(
                    success: false,
                    errorCode: "MALFORMED_REQUEST",
                    message: "The request body could not be parsed as JSON.",
                    correlationId: correlationId));

            // ── Argument / contract violations ──
            case ArgumentNullException argNull:
                return ((int)HttpStatusCode.BadRequest, BuildBody(
                    success: false,
                    errorCode: "ARGUMENT_NULL",
                    message: isDev ? argNull.Message : "A required field was missing.",
                    correlationId: correlationId));

            case ArgumentException argEx:
                return ((int)HttpStatusCode.BadRequest, BuildBody(
                    success: false,
                    errorCode: "ARGUMENT_INVALID",
                    message: isDev ? argEx.Message : "One or more arguments were invalid.",
                    correlationId: correlationId));

            // ── Catch-all: NEVER leak the raw message to clients in Prod ──
            default:
                return ((int)HttpStatusCode.InternalServerError, BuildBody(
                    success: false,
                    errorCode: "SYSTEM_ERROR",
                    message: isDev
                        ? ex.Message
                        : "An unexpected error occurred. Please contact support and quote the reference below.",
                    correlationId: correlationId,
                    exceptionType: isDev ? ex.GetType().Name : null,
                    stackTrace: isDev ? ex.StackTrace : null,
                    innerException: isDev && ex.InnerException != null
                        ? new
                        {
                            type = ex.InnerException.GetType().Name,
                            message = ex.InnerException.Message,
                            stackTrace = ex.InnerException.StackTrace
                        }
                        : null));
        }
    }

    /// <summary>
    /// Best-effort classification of EF Core DbUpdateException using known
    /// SQL Server error numbers in the inner exception. Returns a user-safe
    /// message without leaking column names or table names.
    /// </summary>
    private static string ClassifyDbUpdateError(Microsoft.EntityFrameworkCore.DbUpdateException ex)
    {
        // SqlException inner exception carries a Number; we don't want to take
        // a hard dep on Microsoft.Data.SqlClient here, so introspect via
        // reflection / property name.
        var innerType = ex.InnerException?.GetType().Name ?? string.Empty;
        var innerMsg = ex.InnerException?.Message ?? string.Empty;

        if (innerType == "SqlException")
        {
            // Common SQL Server error codes
            if (innerMsg.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                innerMsg.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
            {
                return "A record with the same unique value already exists.";
            }
            if (innerMsg.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase))
            {
                return "Cannot complete the operation: a related record is missing or in use.";
            }
            if (innerMsg.Contains("CHECK constraint", StringComparison.OrdinalIgnoreCase))
            {
                return "The data does not satisfy one of the required business rules.";
            }
            if (innerMsg.Contains("NULL", StringComparison.OrdinalIgnoreCase) &&
                innerMsg.Contains("not allowed", StringComparison.OrdinalIgnoreCase))
            {
                return "A required field was missing.";
            }
        }

        return "The database rejected the change due to a constraint violation.";
    }

    /// <summary>
    /// Builds the JSON body shape. All non-null extras get included; null
    /// keys are omitted by the serialiser's DefaultIgnoreCondition.
    /// </summary>
    private static object BuildBody(
        bool success,
        string errorCode,
        string message,
        string correlationId,
        object? additionalData = null,
        object? extra = null,
        string? stackTrace = null,
        string? exceptionType = null,
        object? innerException = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["success"] = success,
            ["error"] = message,
            ["message"] = message,        // alias for legacy frontend code that reads err.response.data.message
            ["errorCode"] = errorCode,
            ["correlationId"] = correlationId,
            ["timestamp"] = DateTime.UtcNow,
        };
        if (additionalData != null) body["additionalData"] = additionalData;
        if (stackTrace != null) body["stackTrace"] = stackTrace;
        if (exceptionType != null) body["exceptionType"] = exceptionType;
        if (innerException != null) body["innerException"] = innerException;

        if (extra != null)
        {
            // Merge any extra keys into the body (used by validation errors)
            foreach (var prop in extra.GetType().GetProperties())
            {
                body[prop.Name] = prop.GetValue(extra);
            }
        }

        return body;
    }
}
