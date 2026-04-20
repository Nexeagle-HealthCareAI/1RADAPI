namespace _1Rad.Domain.Exceptions;

/// <summary>
/// Base exception for all domain-specific exceptions
/// </summary>
public abstract class DomainException : Exception
{
    public string ErrorCode { get; }
    public int StatusCode { get; }
    public Dictionary<string, object>? AdditionalData { get; }

    protected DomainException(
        string message,
        string errorCode,
        int statusCode = 400,
        Dictionary<string, object>? additionalData = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
        AdditionalData = additionalData;
    }
}
