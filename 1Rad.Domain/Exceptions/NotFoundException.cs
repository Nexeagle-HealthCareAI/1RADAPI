namespace _1Rad.Domain.Exceptions;

/// <summary>
/// Exception thrown when a requested resource is not found
/// </summary>
public class NotFoundException : DomainException
{
    public NotFoundException(string resourceName, object resourceId)
        : base(
            message: $"{resourceName} with identifier '{resourceId}' was not found.",
            errorCode: "RESOURCE_NOT_FOUND",
            statusCode: 404,
            additionalData: new Dictionary<string, object>
            {
                { "resourceName", resourceName },
                { "resourceId", resourceId }
            })
    {
    }

    public NotFoundException(string message)
        : base(
            message: message,
            errorCode: "RESOURCE_NOT_FOUND",
            statusCode: 404)
    {
    }
}
