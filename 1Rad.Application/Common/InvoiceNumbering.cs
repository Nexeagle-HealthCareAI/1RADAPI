using _1Rad.Application.Interfaces;

namespace _1Rad.Application.Common;

/// <summary>
/// Consecutive, per-centre invoice numbers per Indian financial year (gap analysis P2-11).
///
/// Invoice display IDs used to be "INV-yyyyMMdd-" + 8 random hex characters, so bills
/// were not in a running series. They are now "INV-2026-27-00001", "INV-2026-27-00002", …
/// handed out atomically from dbo.SequenceCounters (key "INVOICE_FY_2026-27"), restarting
/// each 1 April (IST). A number is consumed even if the save that uses it later fails,
/// so a rare gap is possible; no number is ever handed out twice.
/// </summary>
public static class InvoiceNumbering
{
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);

    /// <summary>Financial-year label for a UTC instant, e.g. "2026-27" for 1 Apr 2026 – 31 Mar 2027 IST.</summary>
    public static string FinancialYearLabel(DateTime utc)
    {
        var ist = DateTime.SpecifyKind(utc, DateTimeKind.Utc).Add(Ist);
        var start = ist.Month >= 4 ? ist.Year : ist.Year - 1;
        return $"{start}-{(start + 1) % 100:D2}";
    }

    public static string Format(string fyLabel, int sequence) => $"INV-{fyLabel}-{sequence:D5}";

    public static async Task<string> NextAsync(IApplicationDbContext db, Guid hospitalId, DateTime utcNow, CancellationToken ct)
    {
        var fy = FinancialYearLabel(utcNow);
        var seq = await db.NextSequenceValueAsync(hospitalId, $"INVOICE_FY_{fy}", 1, ct);
        return Format(fy, seq);
    }
}
