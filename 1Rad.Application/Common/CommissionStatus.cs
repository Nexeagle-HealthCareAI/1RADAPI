namespace _1Rad.Application.Common;

/// <summary>
/// Canonical spelling + parsing for <c>ReferralCommission.Status</c>. The column
/// is free text and historically carried mixed casing ("Cancelled" from the
/// appointment flows, "CANCELLED" from the status endpoint, "paid" from ad-hoc
/// callers), so anything that compares it in memory should go through here.
/// (SQL Server's default collation is case-insensitive, so the literal
/// comparisons inside EF queries keep working — this is for in-memory logic and
/// for normalising what gets written.)
/// </summary>
public static class CommissionStatus
{
    public const string Unpaid = "UNPAID";
    public const string Paid = "PAID";
    // Stored as "Cancelled" (title case) by the appointment lifecycle — keep
    // writing that spelling so existing string-equality readers stay correct.
    public const string Cancelled = "Cancelled";

    public static bool IsPaid(string? status) =>
        string.Equals(status?.Trim(), Paid, StringComparison.OrdinalIgnoreCase);

    public static bool IsCancelled(string? status) =>
        string.Equals(status?.Trim(), Cancelled, StringComparison.OrdinalIgnoreCase);

    public static bool IsUnpaid(string? status) =>
        string.Equals(status?.Trim(), Unpaid, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps any accepted spelling to the canonical stored value, or null when the
    /// input is not a recognised status.
    /// </summary>
    public static string? Normalize(string? status)
    {
        if (IsPaid(status)) return Paid;
        if (IsUnpaid(status)) return Unpaid;
        if (IsCancelled(status)) return Cancelled;
        return null;
    }
}
