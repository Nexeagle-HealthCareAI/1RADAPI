using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Common;

/// <summary>
/// Who is doing this to a referral commission, as recorded on the row (UpdatedBy). Taken from the
/// signed-in account, never from the request body: PaidBy / PayeeName are what was typed on the payout
/// form (who handed over the money, who received it), so they can say anything. This is the audit
/// trail's answer to "which login did it".
/// </summary>
public static class CommissionActor
{
    // UpdatedBy is NVARCHAR(200).
    private const int MaxLength = 200;

    public static async Task<string> ResolveAsync(IApplicationDbContext context, CancellationToken ct)
    {
        var userId = context.UserContext.UserId;
        var name = await context.Users.AsNoTracking()
            .Where(u => u.UserId == userId)
            .Select(u => u.FullName)
            .FirstOrDefaultAsync(ct);

        var suffix = $" [{userId}]";
        var label = string.IsNullOrWhiteSpace(name) ? "User" : name.Trim();
        if (label.Length + suffix.Length > MaxLength) label = label[..(MaxLength - suffix.Length)];
        return label + suffix;
    }
}
