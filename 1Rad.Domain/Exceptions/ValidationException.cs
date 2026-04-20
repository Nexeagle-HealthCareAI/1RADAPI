namespace _1Rad.Domain.Exceptions;

/// <summary>
/// Exception thrown when validation fails
/// </summary>
public class ValidationException : DomainException
{
    public Dictionary<string, string[]> Errors { get; }

    public ValidationException(Dictionary<string, string[]> errors)
        : base(
            message: "One or more validation errors occurred.",
            errorCode: "VALIDATION_ERROR",
            statusCode: 400,
            additionalData: new Dictionary<string, object>
            {
                { "errors", errors }
            })
    {
        Errors = errors;
    }

    public ValidationException(string field, string error)
        : this(new Dictionary<string, string[]>
        {
            { field, new[] { error } }
        })
    {
    }

    public ValidationException(string message)
        : base(
            message: message,
            errorCode: "VALIDATION_ERROR",
            statusCode: 400)
    {
        Errors = new Dictionary<string, string[]>();
    }
}
