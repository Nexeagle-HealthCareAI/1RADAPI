namespace _1Rad.Domain.Exceptions;

/// <summary>
/// A bill line looks like one already billed to the same patient today (same test,
/// not on a visit). 409 with errorCode DUPLICATE_BILL_LINE; the screen asks the
/// person to confirm and sends the request again with AllowRepeat.
/// </summary>
public class DuplicateBillLineException : DomainException
{
    public DuplicateBillLineException(string message)
        : base(message: message, errorCode: "DUPLICATE_BILL_LINE", statusCode: 409)
    {
    }
}
