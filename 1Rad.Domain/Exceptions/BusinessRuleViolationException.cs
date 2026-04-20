namespace _1Rad.Domain.Exceptions;

/// <summary>
/// Exception thrown when a business rule is violated
/// </summary>
public class BusinessRuleViolationException : DomainException
{
    public BusinessRuleViolationException(string message, string errorCode = "BUSINESS_RULE_VIOLATION")
        : base(
            message: message,
            errorCode: errorCode,
            statusCode: 422)
    {
    }

    public BusinessRuleViolationException(string message, string errorCode, Dictionary<string, object> additionalData)
        : base(
            message: message,
            errorCode: errorCode,
            statusCode: 422,
            additionalData: additionalData)
    {
    }
}
