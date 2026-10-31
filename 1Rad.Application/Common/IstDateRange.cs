namespace _1Rad.Application.Common;

/// <summary>
/// Converts a bare calendar-date filter — the "YYYY-MM-DD" a date picker or
/// the frontend's own getIstDateStr() sends, which ASP.NET model-binds as a
/// DateTimeKind.Unspecified midnight — into the correct UTC instant boundary
/// for querying columns that store proper UTC timestamps (CreatedAt,
/// PaidAt, UpdatedAt, etc — populated client-side from
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
///
/// Not every date column is UTC: Appointment.DateTime and the ServiceDate copied
/// from it hold India clock time (the centre's rule — see the Clock* helpers).
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

    /// <summary>
    /// A stored UTC instant expressed as IST wall-clock time — for display and for
    /// bucketing by IST day / hour (a UTC `.Date` or `.Hour` puts an evening visit
    /// on the wrong day).
    /// </summary>
    public static DateTime ToIst(DateTime utc) => utc + Offset;

    // ── Columns that hold India clock time ──────────────────────────────────
    // Appointment.DateTime is stored as the India wall-clock time of the visit
    // (what the front desk picked — no conversion), and so are the ServiceDate
    // copies on Invoice and ReferralCommission. Filter and bucket those with the
    // helpers below, never with ToUtcStart/ToIst (that shifts them 5½ hours).

    /// <summary>First instant of the IST day `date` names, for an IST-clock column.</summary>
    public static DateTime ClockStart(DateTime date) => date.Date;

    /// <summary>Last instant of the IST day `date` names, for an IST-clock column (inclusive).</summary>
    public static DateTime ClockEndInclusive(DateTime date) => date.Date.AddDays(1).AddTicks(-1);

    /// <summary>Now, as India clock time (Kind Unspecified) — what to stamp an IST-clock column with.</summary>
    public static DateTime NowIst() => DateTime.SpecifyKind(DateTime.UtcNow + Offset, DateTimeKind.Unspecified);

    /// <summary>An IST-clock value as the UTC instant it names (for FHIR, DICOM, ISO strings).</summary>
    public static DateTime ClockToUtc(DateTime ist) => DateTime.SpecifyKind(ist - Offset, DateTimeKind.Utc);

    /// <summary>
    /// A visit time arriving from a client or integration, turned into IST clock time.
    /// A value marked UTC (an ISO string ending in Z, or a FHIR instant) is converted;
    /// a bare value (Kind Unspecified — "2026-10-31T10:30") is already India time.
    /// A Local value is the server's own clock (never sent by clients) and is converted
    /// through UTC.
    /// </summary>
    public static DateTime NormalizeToClock(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => DateTime.SpecifyKind(value + Offset, DateTimeKind.Unspecified),
        DateTimeKind.Local => DateTime.SpecifyKind(value.ToUniversalTime() + Offset, DateTimeKind.Unspecified),
        _ => value,
    };
}
