namespace _1Rad.Domain.Exceptions;

/// <summary>
/// Exception thrown when user is not authenticated
/// </summary>
public class UnauthorizedException : DomainException
{
    public UnauthorizedException(string message = "Authentication is required to access this resource.")
        : base(
            message: message,
            errorCode: "UNAUTHORIZED",
            statusCode: 401)
    {
    }

    public UnauthorizedException(string message, string errorCode)
        : base(
            message: message,
            errorCode: errorCode,
            statusCode: 401)
    {
    }
}
