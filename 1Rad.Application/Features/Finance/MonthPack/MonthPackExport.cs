using ClosedXML.Excel;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.MonthPack;

/// <summary>
/// Money ▸ Reports ▸ "Month pack for your CA": one Excel file for a month (India
/// dates) with a Summary sheet whose numbers tie out, and one sheet per ledger:
///   1 Sales register  – every bill made in the month, cancelled ones marked
///   2 Receipts        – every payment and refund row, with receipt number and mode
///   3 Credit notes    – credit notes and refund vouchers issued in the month
///   4 Expenses        – expenses paid in the month (by the day paid)
///   5 Partner payouts – payouts with TDS held
///   6 Day closes      – the cash counted each day against what was expected
///   7 Still to collect – bills of the month with money still due today
/// Summary: Billed (net of discounts) − credit notes = net; collected + due = billed.
/// </summary>
public record ExportMonthPackQuery(int Year, int Month) : IRequest<MonthPackFile>;
public record MonthPackFile(byte[] Content, string FileName);

public class ExportMonthPackQueryHandler : IRequestHandler<ExportMonthPackQuery, MonthPackFile>
{
    private readonly IApplicationDbContext _db;
    public ExportMonthPackQueryHandler(IApplicationDbContext db) => _db = db;

    private static DateTime Ist(DateTime utc) => IstDateRange.ToIst(utc);

    public async Task<MonthPackFile> Handle(ExportMonthPackQuery q, CancellationToken ct)
    {
        if (q.Month is < 1 or > 12 || q.Year is < 2020 or > 2100) throw new ValidationException("Pick a month.");
        var hospitalId = _db.UserContext.HospitalId;
        var first = new DateTime(q.Year, q.Month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var start = IstDateRange.ToUtcStart(first);
        var end = IstDateRange.ToUtcEndInclusive(last);

        var bills = await _db.Invoices.AsNoTracking()
            .Where(i => i.HospitalId == hospitalId && i.DeletedAt == null && i.CreatedAt >= start && i.CreatedAt <= end)
            .OrderBy(i => i.CreatedAt)
            .Select(i => new { i.Id, i.InvoiceId, i.CreatedAt, i.PatientName, i.GrossAmount, i.CentreDiscount, i.ReferrerDiscount,
                               i.InstitutionalDeduction, i.TotalAmount, i.PaidAmount, i.Status, i.DiscountReason })
            .ToListAsync(ct);
        var payments = await _db.Payments.AsNoTracking()
            .Where(p => p.HospitalId == hospitalId && p.CreatedAt >= start && p.CreatedAt <= end)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new { p.CreatedAt, p.ReceiptNumber, Bill = p.Invoice.InvoiceId, Patient = p.Invoice.PatientName, p.PaymentMethod,
                               p.Reference, p.Amount, p.Kind, p.CreatedByUserId })
            .ToListAsync(ct);
        var notes = await _db.CreditNotes.AsNoTracking()
            .Where(n => n.HospitalId == hospitalId && n.CreatedAt >= start && n.CreatedAt <= end)
            .OrderBy(n => n.CreatedAt).ToListAsync(ct);
        var expenses = await _db.Expenses.AsNoTracking()
            .Where(e => e.HospitalId == hospitalId && e.DeletedAt == null && e.Status == "Paid"
                        && (e.PaidAt ?? e.TransactionDate) >= start && (e.PaidAt ?? e.TransactionDate) <= end)
            .OrderBy(e => e.PaidAt ?? e.TransactionDate)
            .Select(e => new { At = e.PaidAt ?? e.TransactionDate, e.Category, e.Description, e.VendorName, e.PaymentMode, e.ReferenceNumber, e.Amount, e.TaxAmount })
            .ToListAsync(ct);
        var payouts = await _db.ReferralPayouts.AsNoTracking()
            .Where(r => r.HospitalId == hospitalId && r.PaidAt >= start && r.PaidAt <= end)
            .OrderBy(r => r.PaidAt).ToListAsync(ct);
        var closes = await _db.DayCloses.AsNoTracking()
            .Where(d => d.HospitalId == hospitalId && d.BusinessDate >= first && d.BusinessDate <= last)
            .OrderBy(d => d.BusinessDate).ToListAsync(ct);

        var userIds = payments.Select(p => p.CreatedByUserId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        var names = userIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.Users.AsNoTracking().IgnoreQueryFilters().Where(u => userIds.Contains(u.UserId)).ToDictionaryAsync(u => u.UserId, u => u.FullName, ct);

        using var wb = new XLWorkbook();
        var live = bills.Where(b => b.Status != "CANCELLED").ToList();
        decimal billed = live.Sum(b => b.TotalAmount);
        decimal collected = live.Sum(b => Math.Min(b.PaidAmount, b.TotalAmount));
        decimal cn = notes.Where(n => n.Kind == CreditNoteKinds.CreditNote).Sum(n => n.Amount);
        decimal received = payments.Where(p => p.PaymentMethod != "ADVANCE").Sum(p => p.Amount);

        // Summary
        var s = wb.AddWorksheet("Summary");
        string centre = await _db.Hospitals.AsNoTracking().Where(h => h.HospitalId == hospitalId).Select(h => h.HospitalName).FirstOrDefaultAsync(ct) ?? "";
        s.Cell(1, 1).Value = $"{centre} · {first:MMMM yyyy}";
        s.Cell(1, 1).Style.Font.Bold = true; s.Cell(1, 1).Style.Font.FontSize = 14;
        var rows = new (string, object)[]
        {
            ("Bills made (not cancelled)", live.Count),
            ("Bills cancelled", bills.Count - live.Count),
            ("Gross before discounts", live.Sum(b => b.GrossAmount)),
            ("Discounts given", live.Sum(b => b.CentreDiscount + b.ReferrerDiscount + b.InstitutionalDeduction)),
            ("Billed (after discounts)", billed),
            ("Credit notes issued", -cn),
            ("Net billed", billed - cn),
            ("Collected on these bills", collected),
            ("Still to collect on these bills", billed - collected),
            ("Money received in the month (all bills, refunds taken off)", received),
            ("Expenses paid", expenses.Sum(e => e.Amount)),
            ("Partner payouts (gross)", payouts.Sum(p => p.Gross)),
            ("TDS held on payouts", payouts.Sum(p => p.TdsAmount)),
            ("Cash counted short (−) / over (+)", closes.Sum(d => d.Variance)),
        };
        for (var i = 0; i < rows.Length; i++)
        {
            s.Cell(i + 3, 1).Value = rows[i].Item1;
            Put(s.Cell(i + 3, 2), rows[i].Item2);
        }
        s.Cell(rows.Length + 4, 1).Value = "Check: collected + still to collect = billed; money received differs because it also counts older bills and advances.";
        s.Columns().AdjustToContents();

        // 1 Sales register
        var w = wb.AddWorksheet("1 Sales register");
        Header(w, "Date", "Bill", "Patient", "Gross", "Centre discount", "Partner discount", "Deduction", "Billed", "Paid", "Due", "Status", "Discount reason");
        var r = 2;
        foreach (var b in bills)
        {
            Row(w, r++, Ist(b.CreatedAt).ToString("dd-MM-yyyy"), b.InvoiceId, b.PatientName ?? "", b.GrossAmount, b.CentreDiscount, b.ReferrerDiscount,
                b.InstitutionalDeduction, b.TotalAmount, b.PaidAmount, Math.Max(0, b.TotalAmount - b.PaidAmount), b.Status, b.DiscountReason ?? "");
        }

        // 2 Receipts
        w = wb.AddWorksheet("2 Receipts");
        Header(w, "Date", "Time", "Receipt", "Bill", "Patient", "Mode", "Reference", "Amount", "Kind", "Taken by");
        r = 2;
        foreach (var p in payments)
        {
            var at = Ist(p.CreatedAt);
            Row(w, r++, at.ToString("dd-MM-yyyy"), at.ToString("HH:mm"), p.ReceiptNumber ?? "", p.Bill, p.Patient ?? "", p.PaymentMethod ?? "",
                p.Reference ?? "", p.Amount, p.Kind ?? "", p.CreatedByUserId.HasValue && names.TryGetValue(p.CreatedByUserId.Value, out var n) ? n : "");
        }

        // 3 Credit notes
        w = wb.AddWorksheet("3 Credit notes");
        Header(w, "Date", "Number", "Kind", "Bill", "Patient", "Amount", "Refunded", "How", "Bill before", "Bill after", "Reason");
        r = 2;
        foreach (var n in notes)
            Row(w, r++, Ist(n.CreatedAt).ToString("dd-MM-yyyy"), n.Number, n.Kind == CreditNoteKinds.CreditNote ? "Credit note" : "Refund voucher",
                n.InvoiceNumber, n.PatientName ?? "", n.Amount, n.RefundedAmount, n.RefundModes ?? "", n.BillTotalBefore, n.BillTotalAfter, n.Reason);

        // 4 Expenses
        w = wb.AddWorksheet("4 Expenses");
        Header(w, "Date paid", "Head", "For what", "Paid to", "Mode", "Reference", "Amount", "Tax in it");
        r = 2;
        foreach (var e in expenses)
            Row(w, r++, Ist(e.At).ToString("dd-MM-yyyy"), e.Category, e.Description, e.VendorName ?? "", e.PaymentMode ?? "", e.ReferenceNumber ?? "", e.Amount, e.TaxAmount);

        // 5 Partner payouts
        w = wb.AddWorksheet("5 Partner payouts");
        Header(w, "Date", "Payout", "Paid to", "PAN", "Gross", "Recovered", "TDS", "Net paid", "Mode", "Reference");
        r = 2;
        foreach (var p in payouts)
            Row(w, r++, Ist(p.PaidAt).ToString("dd-MM-yyyy"), p.PayoutNumber, p.PayeeName, p.PayeePan ?? "", p.Gross, p.DeficitRecovered, p.TdsAmount, p.Net, p.Mode, p.Reference ?? "");

        // 6 Day closes
        w = wb.AddWorksheet("6 Day closes");
        Header(w, "Day", "Opening cash", "Cash in − out", "Should be", "Counted", "Short / over", "Why", "Kept for tomorrow", "Handed over", "Handed to", "Closed by");
        r = 2;
        foreach (var d in closes)
            Row(w, r++, d.BusinessDate.ToString("dd-MM-yyyy"), d.OpeningFloat, d.ExpectedCash, d.OpeningFloat + d.ExpectedCash, d.CountedCash, d.Variance,
                d.DifferenceReason ?? d.Note ?? "", d.FloatKept, d.HandedOver, d.HandedTo ?? "", d.ClosedByName ?? "");

        // 7 Still to collect
        w = wb.AddWorksheet("7 Still to collect");
        Header(w, "Bill date", "Bill", "Patient", "Billed", "Paid", "Due today");
        r = 2;
        foreach (var b in live.Where(b => b.TotalAmount - b.PaidAmount > 0.009m))
            Row(w, r++, Ist(b.CreatedAt).ToString("dd-MM-yyyy"), b.InvoiceId, b.PatientName ?? "", b.TotalAmount, b.PaidAmount, b.TotalAmount - b.PaidAmount);

        foreach (var sheet in wb.Worksheets.Skip(1)) { sheet.SheetView.FreezeRows(1); sheet.Columns().AdjustToContents(1, 60); }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return new MonthPackFile(ms.ToArray(), $"Month-pack-{first:yyyy-MM}.xlsx");
    }

    private static void Header(IXLWorksheet w, params string[] titles)
    {
        for (var i = 0; i < titles.Length; i++)
        {
            w.Cell(1, i + 1).Value = titles[i];
            w.Cell(1, i + 1).Style.Font.Bold = true;
            w.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF2FF");
        }
    }

    private static void Row(IXLWorksheet w, int row, params object[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            Put(w.Cell(row, i + 1), values[i]);
        }
    }

    private static void Put(IXLCell c, object? v)
    {
        switch (v)
        {
            case decimal d: c.Value = d; c.Style.NumberFormat.Format = "#,##0.00"; break;
            case int n: c.Value = n; break;
            case null: c.Value = string.Empty; break;
            default: c.Value = v.ToString(); break;
        }
    }
}
