using _1Rad.Application.Interfaces;

namespace _1Rad.Application.Common;

/// <summary>
/// Consecutive, per-centre invoice numbers per Indian financial year (gap analysis P2-11).
///
/// Invoice display IDs used to be "INV-yyyyMMdd-" + 8 random hex characters, so bills
/// were not in a running series. They are now a running series per financial year ("INV2627-00001", …; see Format)
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

    /// <summary>
    /// Review B26: GST rules 46 and 49 cap a bill's serial number at 16 characters,
    /// and "INV-2026-27-00001" was 17. Now "INV2627-00001" (13; 14 past 99,999 a
    /// year). The running number per financial year is unchanged, so the series
    /// simply continues in the shorter form.
    /// </summary>
    public static string Format(string fyLabel, int sequence) => $"INV{Compact(fyLabel)}-{sequence:D5}";

    /// <summary>"2026-27" → "2627".</summary>
    public static string Compact(string fyLabel)
    {
        var parts = fyLabel.Split('-');
        return parts.Length == 2 && parts[0].Length == 4 ? parts[0][2..] + parts[1] : fyLabel.Replace("-", "");
    }

    public static async Task<string> NextAsync(IApplicationDbContext db, Guid hospitalId, DateTime utcNow, CancellationToken ct)
    {
        var fy = FinancialYearLabel(utcNow);
        var seq = await db.NextSequenceValueAsync(hospitalId, $"INVOICE_FY_{fy}", 1, ct);
        return Format(fy, seq);
    }
}
