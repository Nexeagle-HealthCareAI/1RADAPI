namespace _1Rad.Application.Common;

/// <summary>
/// What a visit's status means for referral analytics: did the patient actually turn up?
///
/// A referral is only worth counting (and paying for) once the patient ARRIVES. Source
/// Analytics used to count every non-cancelled appointment, so a booking that never showed
/// up, or one that is still ahead, was reported as a "scan" from that partner.
///
/// Stored statuses: BOOKED (aka scheduled) -> CONFIRMED (= arrived, sets ArrivedAt) ->
/// IN_PROGRESS -> SCANNED -> REPORTED -> DELIVERED / COMPLETED, or CANCELLED. There is no
/// stored "no-show": the Appointments board derives it as a past-dated booking that never
/// arrived, and so does this.
/// </summary>
public static class AppointmentAttendance
{
    public const string Attended = "ATTENDED";
    public const string NoShow = "NO_SHOW";
    public const string Upcoming = "UPCOMING";

    // Canonical spellings, both underscore and space variants (legacy rows differ).
    public static readonly string[] AttendedStatuses =
    {
        "CONFIRMED", "ARRIVED", "IN_PROGRESS", "IN PROGRESS", "SCANNED", "REPORTED", "DELIVERED", "COMPLETED",
    };

    private static readonly HashSet<string> AttendedSet = new(AttendedStatuses, StringComparer.OrdinalIgnoreCase);

    public static bool IsCancelled(string? status) =>
        string.Equals(status?.Trim(), "CANCELLED", StringComparison.OrdinalIgnoreCase);

    public static bool IsAttended(string? status, DateTime? arrivedAt) =>
        arrivedAt.HasValue || AttendedSet.Contains((status ?? string.Empty).Trim());

    /// <summary>
    /// ATTENDED, NO_SHOW (not attended and its IST day is already over) or UPCOMING (not
    /// attended yet, today or later). Callers pass cancelled visits nowhere near this.
    /// </summary>
    public static string Classify(string? status, DateTime? arrivedAt, DateTime visitUtc, DateTime nowUtc)
    {
        if (IsAttended(status, arrivedAt)) return Attended;
        return IstDateRange.ToIst(visitUtc).Date < IstDateRange.ToIst(nowUtc).Date ? NoShow : Upcoming;
    }
}
