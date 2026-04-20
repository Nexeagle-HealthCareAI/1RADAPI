namespace _1Rad.Domain.Exceptions;

/// <summary>
/// Exception thrown when user is authenticated but not authorized to perform an action
/// </summary>
public class ForbiddenException : DomainException
{
    public ForbiddenException(string message = "You do not have permission to access this resource.")
        : base(
            message: message,
            errorCode: "FORBIDDEN",
            statusCode: 403)
    {
    }

    public ForbiddenException(string message, string errorCode)
        : base(
            message: message,
            errorCode: errorCode,
            statusCode: 403)
    {
    }
}
