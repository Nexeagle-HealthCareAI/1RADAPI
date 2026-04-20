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
            _logger.LogError(ex, "An unhandled exception occurred: {Message}", ex.Message);
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
            errorCode = "FORBIDDEN",
            timestamp = DateTime.UtcNow
        };

        return ((int)HttpStatusCode.Forbidden, errorResponse);
    }

    private (int StatusCode, object ErrorResponse) HandleGenericException(Exception exception)
    {
        _logger.LogError(exception, "Unhandled exception: {Message}", exception.Message);

        var isDevelopment = _env.IsDevelopment();
        var errorMessage = isDevelopment 
            ? exception.Message 
            : "An unexpected error occurred. Our team has been notified. Please try again later.";

        var errorResponse = new
        {
            success = false,
            error = errorMessage,
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
