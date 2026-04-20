namespace _1Rad.Application.Common.Models;

/// <summary>
/// Standardized API response wrapper
/// </summary>
public class ApiResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public Dictionary<string, string[]>? ValidationErrors { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public static ApiResponse<T> SuccessResponse(T data)
    {
        return new ApiResponse<T>
        {
            Success = true,
            Data = data
        };
    }

    public static ApiResponse<T> ErrorResponse(string error, string errorCode)
    {
        return new ApiResponse<T>
        {
            Success = false,
            Error = error,
            ErrorCode = errorCode
        };
    }

    public static ApiResponse<T> ValidationErrorResponse(Dictionary<string, string[]> validationErrors)
    {
        return new ApiResponse<T>
        {
            Success = false,
            Error = "One or more validation errors occurred.",
            ErrorCode = "VALIDATION_ERROR",
            ValidationErrors = validationErrors
        };
    }
}

/// <summary>
/// Non-generic API response for operations that don't return data
/// </summary>
public class ApiResponse : ApiResponse<object>
{
    public static new ApiResponse SuccessResponse(string? message = null)
    {
        var response = new ApiResponse();
        response.Success = true;
        response.Data = message != null ? new { message } : null;
        return response;
    }

    public static new ApiResponse ErrorResponse(string error, string errorCode)
    {
        var response = new ApiResponse();
        response.Success = false;
        response.Error = error;
        response.ErrorCode = errorCode;
        return response;
    }
}

