using System.Net;
using System.Text.Json;
using _1Rad.Domain.Exceptions;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger, IHostEnvironment env)
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
            var requestPath = context.Request.Path;
            var requestMethod = context.Request.Method;
            _logger.LogError(ex, "Unhandled exception on {Method} {Path}: {Message}", requestMethod, requestPath, ex.Message);
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        context.Response.ContentType = "application/json";

        var (statusCode, errorResponse) = exception switch
        {
            DomainException domainEx => HandleDomainException(domainEx),
            FluentValidation.ValidationException validationEx => HandleFluentValidationException(validationEx),
            UnauthorizedAccessException => HandleUnauthorizedAccessException(),
            _ => HandleGenericException(exception)
        };

        context.Response.StatusCode = statusCode;

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = _env.IsDevelopment()
        };

        var json = JsonSerializer.Serialize(errorResponse, options);
        await context.Response.WriteAsync(json);
    }

    private (int StatusCode, object ErrorResponse) HandleDomainException(DomainException exception)
    {
        var errorResponse = new
        {
            success = false,
            error = exception.Message,
            message = exception.Message, // Alias for frontend compatibility
            errorCode = exception.ErrorCode,
            timestamp = DateTime.UtcNow,
            path = exception.AdditionalData?.ContainsKey("path") == true 
                ? exception.AdditionalData["path"] 
                : null,
            additionalData = exception.AdditionalData,
            stackTrace = _env.IsDevelopment() ? exception.StackTrace : null
        };

        return (exception.StatusCode, errorResponse);
    }

    private (int StatusCode, object ErrorResponse) HandleFluentValidationException(FluentValidation.ValidationException exception)
    {
        var errors = exception.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ErrorMessage).ToArray()
            );

        var errorResponse = new
        {
            success = false,
            error = "One or more validation errors occurred.",
            message = "One or more validation errors occurred.", // Alias
            errorCode = "VALIDATION_ERROR",
            errors = errors,
            timestamp = DateTime.UtcNow,
            stackTrace = _env.IsDevelopment() ? exception.StackTrace : null
        };

        return ((int)HttpStatusCode.BadRequest, errorResponse);
    }

    private (int StatusCode, object ErrorResponse) HandleUnauthorizedAccessException()
    {
        var errorResponse = new
        {
            success = false,
            error = "You don't have permission to access this resource.",
            message = "You don't have permission to access this resource.", // Alias
            errorCode = "FORBIDDEN",
            timestamp = DateTime.UtcNow
        };

        return ((int)HttpStatusCode.Forbidden, errorResponse);
    }

    private (int StatusCode, object ErrorResponse) HandleGenericException(Exception exception)
    {
        _logger.LogError(exception, "Unhandled exception: {Message}", exception.Message);

        var isDevelopment = _env.IsDevelopment();
        var msg = exception.Message ?? "";
        
        // Return 400 for Database Update Exceptions (like unique constraint violations)
        if (exception is Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            var errorResponseDb = new
            {
                success = false,
                error = "Database operation failed. The record might already exist or violates a constraint.",
                message = "Database operation failed. The record might already exist or violates a constraint.",
                errorCode = "DB_CONSTRAINT_ERROR",
                timestamp = DateTime.UtcNow
            };
            return ((int)HttpStatusCode.BadRequest, errorResponseDb);
        }

        var errorMessage = isDevelopment 
            ? msg 
            : (msg.Contains("CONFIG_MISSING") || msg.Contains("FAILURE") || msg.Contains("Exception") || msg.Contains("Error") || msg.Contains("Sql") || msg.Contains("DbUpdate") || msg.Contains("Azure")
                ? msg 
                : "An unexpected error occurred. Our team has been notified. Please try again later.");

        var errorResponse = new
        {
            success = false,
            error = errorMessage,
            message = errorMessage, // Added to fix frontend swallowing errors where it expects err.response.data.message
            errorCode = "SYSTEM_ERROR",
            timestamp = DateTime.UtcNow,
            stackTrace = isDevelopment ? exception.StackTrace : null,
            innerException = isDevelopment && exception.InnerException != null
                ? new
                {
                    message = exception.InnerException.Message,
                    stackTrace = exception.InnerException.StackTrace,
                    type = exception.InnerException.GetType().Name
                }
                : null,
            exceptionType = isDevelopment ? exception.GetType().Name : null
        };

        return ((int)HttpStatusCode.InternalServerError, errorResponse);
    }
}
