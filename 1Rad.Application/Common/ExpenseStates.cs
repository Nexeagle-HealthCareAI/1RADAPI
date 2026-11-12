using _1Rad.Domain.Exceptions;

namespace _1Rad.Application.Common;

/// <summary>
/// The states an expense can be in (review B28: it used to accept any text).
/// "Paid" is the only one in which money has left; the others are "to pay".
/// </summary>
public static class ExpenseStates
{
    public static readonly string[] All = { "Draft", "Pending", "Approved", "Paid" };

    public static string Normalise(string? status)
    {
        var s = (status ?? string.Empty).Trim();
        var hit = All.FirstOrDefault(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase));
        return hit ?? throw new ValidationException("An expense is Draft, Pending, Approved or Paid.");
    }
}
