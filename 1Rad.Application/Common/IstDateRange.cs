namespace _1Rad.Application.Common;

/// <summary>
/// Converts a bare calendar-date filter — the "YYYY-MM-DD" a date picker or
/// the frontend's own getIstDateStr() sends, which ASP.NET model-binds as a
/// DateTimeKind.Unspecified midnight — into the correct UTC instant boundary
/// for querying columns that store proper UTC timestamps (ServiceDate,
/// CreatedAt, TransactionDate, etc — populated client-side from
/// new Date().toISOString() or server-side from DateTime.UtcNow).
///
/// Without this, a bare "2026-09-16" — meant as "the IST calendar day
/// Sept 16" — gets compared as if it meant midnight UTC, which is actually
/// 5:30am IST. Every day boundary silently shifts by 5.5 hours: an
/// appointment at 1am IST on the 16th (7:30pm UTC on the 15th) falls outside
/// a "StartDate=Sept 16" filter, while one at 1am IST on the 17th falls
/// inside it — disagreeing with every other IST-aware view in the app
/// (Revenue's own getIstDateStr comparisons, the offline Dexie cache's
/// istDayStartMs, etc), which is exactly the kind of "the numbers don't
/// match" symptom this fixes.
/// </summary>
public static class IstDateRange
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(5.5);

    /// <summary>
    /// The first UTC instant of the IST calendar day `date` names (only its
    /// Date component is used — any time-of-day/Kind on the input is
    /// ignored, since callers pass a bare calendar-date filter).
    /// </summary>
    public static DateTime ToUtcStart(DateTime date) => date.Date - Offset;

    /// <summary>
    /// The last UTC instant still within the IST calendar day `date` names —
    /// an inclusive upper bound for a `&lt;=` filter.
    /// </summary>
    public static DateTime ToUtcEndInclusive(DateTime date) => date.Date.AddDays(1) - Offset - TimeSpan.FromTicks(1);
}
