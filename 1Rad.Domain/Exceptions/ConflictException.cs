namespace _1Rad.Domain.Exceptions;

/// <summary>
/// Exception thrown when there is a conflict with existing data
/// </summary>
public class ConflictException : DomainException
{
    public ConflictException(string message)
        : base(
            message: message,
            errorCode: "CONFLICT",
            statusCode: 409)
    {
    }

    public ConflictException(string resourceName, string conflictReason)
        : base(
            message: $"{resourceName} already exists. {conflictReason}",
            errorCode: "RESOURCE_CONFLICT",
            statusCode: 409,
            additionalData: new Dictionary<string, object>
            {
                { "resourceName", resourceName },
                { "reason", conflictReason }
            })
    {
    }
}
