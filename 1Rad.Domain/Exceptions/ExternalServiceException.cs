namespace _1Rad.Domain.Exceptions;

/// <summary>
/// Exception thrown when an external service fails
/// </summary>
public class ExternalServiceException : DomainException
{
    public ExternalServiceException(string serviceName, string message, Exception? innerException = null)
        : base(
            message: $"External service '{serviceName}' failed: {message}",
            errorCode: "EXTERNAL_SERVICE_ERROR",
            statusCode: 502,
            additionalData: new Dictionary<string, object>
            {
                { "serviceName", serviceName },
                { "serviceMessage", message }
            },
            innerException: innerException)
    {
    }
}
