using System.Text.RegularExpressions;
using ClosedXML.Excel;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Constants;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Gstr1;

/// <summary>
/// The month's GSTR-1 working sheet (review B10). A diagnostic centre bills patients,
/// who are unregistered, so its outward supplies are B2C, mostly exempt (SAC 9993):
///
///   Summary — totals for the month and how they were worked out.
///   b2cs    — Table 7: taxable B2C supplies by place of supply and rate.
///   exemp   — Table 8: nil-rated / exempt / non-GST supplies.
///   hsn     — Table 12: summary by SAC and rate (B2C).
///   docs    — Table 13: documents issued (bills, credit notes, refund vouchers).
///   Bills, Credit notes — the registers behind the numbers.
///
/// A bill belongs to the month it was issued (CreatedAt, IST). Prices are GST-
/// inclusive: the tax is split out of each line, after the bill's discount is spread
/// over its lines (same rule as the printed bill). Cancelled and deleted bills carry
/// no value but are counted as cancelled in Table 13. A credit note against a bill
/// from an earlier month reduces this month's figures (that month is already filed);
/// one against this month's own bill is already reflected in the bill's current value.
/// Sheet and column names follow the GST offline tool's B2C templates so the figures
/// can be pasted across. It is a working sheet for the centre's accountant — the
/// return itself is filed on the GST portal.
/// </summary>
public record ExportGstr1Query(int Year, int Month) : IRequest<Gstr1File>;

public record Gstr1File(byte[] Content, string FileName);

public class ExportGstr1QueryHandler : IRequestHandler<ExportGstr1Query, Gstr1File>
{
    private const string NewSeries = @"^INV\d{4}-\d{5,}$";
    private readonly IApplicationDbContext _db;
    public ExportGstr1QueryHandler(IApplicationDbContext db) => _db = db;

    private sealed record Line(Guid InvoiceId, string Sac, decimal Rate, decimal Value, int Qty);

    public async Task<Gstr1File> Handle(ExportGstr1Query q, CancellationToken ct)
    {
        if (q.Month is < 1 or > 12 || q.Year is < 2017 or > 2100) throw new ValidationException("Pick a valid month.");
        var hospitalId = _db.UserContext.HospitalId;
        var firstDay = new DateTime(q.Year, q.Month, 1);
        var start = IstDateRange.ToUtcStart(firstDay);
        var endExcl = IstDateRange.ToUtcStart(firstDay.AddMonths(1));

        var hospital = await _db.Hospitals.AsNoTracking().Where(h => h.HospitalId == hospitalId)
            .Select(h => new { h.HospitalName, h.GSTIN }).FirstOrDefaultAsync(ct);
        var gstin = (hospital?.GSTIN ?? string.Empty).Trim().ToUpperInvariant();
        var pos = GstStates.PlaceOfSupply(gstin);

        var bills = await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.HospitalId == hospitalId && i.CreatedAt >= start && i.CreatedAt < endExcl)
            .Select(i => new { i.Id, i.InvoiceId, i.CreatedAt, i.PatientName, i.GrossAmount, i.TotalAmount, i.Status, i.DeletedAt })
            .OrderBy(i => i.CreatedAt).ToListAsync(ct);
        var notes = await _db.CreditNotes.AsNoTracking()
            .Where(c => c.HospitalId == hospitalId && c.CreatedAt >= start && c.CreatedAt < endExcl)
            .OrderBy(c => c.CreatedAt).ToListAsync(ct);

        var itemInvoiceIds = bills.Select(b => b.Id).Concat(notes.Select(n => n.InvoiceId)).Distinct().ToList();
        var items = new List<(Guid InvoiceId, decimal Amount, int Quantity, bool IsFree, string? Sac, decimal? Rate)>();
        foreach (var chunk in itemInvoiceIds.Chunk(1000))
        {
            var ids = chunk.ToList();
            items.AddRange((await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
                    .Where(i => ids.Contains(i.Id))
                    .SelectMany(i => i.Items.Select(it => new { it.InvoiceId, it.Amount, it.Quantity, it.IsFree, it.SacCode, it.GstRate }))
                    .ToListAsync(ct))
                .Select(i => (i.InvoiceId, i.Amount, i.Quantity, i.IsFree, i.SacCode, i.GstRate)));
        }
        var itemsByInvoice = items.ToLookup(i => i.InvoiceId);

        // A bill's (or a credit note's) value spread over its lines, by SAC and rate.
        List<Line> Spread(Guid invoiceId, decimal value)
        {
            var lines = itemsByInvoice[invoiceId].Where(i => !i.IsFree).ToList();
            var gross = lines.Sum(i => i.Amount * i.Quantity);
            if (gross <= 0 || value <= 0)
                return value > 0 ? new List<Line> { new(invoiceId, GstDefaults.DiagnosticSac, 0m, value, 1) } : new List<Line>();
            return lines.Select(i => new Line(invoiceId, string.IsNullOrWhiteSpace(i.Sac) ? GstDefaults.DiagnosticSac : i.Sac!.Trim(),
                i.Rate ?? 0m, i.Amount * i.Quantity * value / gross, Math.Max(1, i.Quantity))).ToList();
        }

        bool Live(string? status, DateTime? deletedAt) =>
            deletedAt == null && !string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase);

        var billLines = bills.Where(b => Live(b.Status, b.DeletedAt)).SelectMany(b => Spread(b.Id, b.TotalAmount)).ToList();
        var thisMonthBills = bills.Select(b => b.Id).ToHashSet();
        var nettedNotes = notes.Where(n => n.Kind == CreditNoteKinds.CreditNote && !thisMonthBills.Contains(n.InvoiceId)).ToList();
        var noteLines = nettedNotes.SelectMany(n => Spread(n.InvoiceId, n.Amount)).ToList();

        static decimal Taxable(decimal value, decimal rate) => rate <= 0 ? value : value / (1 + rate / 100m);
        static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

        using var wb = new XLWorkbook();
        var period = $"{q.Month:D2}{q.Year}";

        // ── b2cs (Table 7) ──
        var b2cs = billLines.Select(l => (l.Rate, Value: l.Value)).Concat(noteLines.Select(l => (l.Rate, Value: -l.Value)))
            .Where(x => x.Rate > 0).GroupBy(x => x.Rate).OrderBy(g => g.Key)
            .Select(g => (Rate: g.Key, Value: g.Sum(x => x.Value))).ToList();
        var ws = wb.AddWorksheet("b2cs");
        Header(ws, "Type", "Place Of Supply", "Rate", "Applicable % of Tax Rate", "Taxable Value", "Cess Amount", "E-Commerce GSTIN");
        var row = 2;
        foreach (var g in b2cs)
        {
            ws.Cell(row, 1).Value = "OE";
            ws.Cell(row, 2).Value = pos;
            ws.Cell(row, 3).Value = g.Rate;
            ws.Cell(row, 5).Value = R2(Taxable(g.Value, g.Rate));
            ws.Cell(row, 6).Value = 0;
            row++;
        }
        if (b2cs.Count == 0) ws.Cell(2, 1).Value = "No taxable supplies this month.";

        // ── exemp (Table 8) ──
        var exemptValue = R2(billLines.Where(l => l.Rate <= 0).Sum(l => l.Value) - noteLines.Where(l => l.Rate <= 0).Sum(l => l.Value));
        ws = wb.AddWorksheet("exemp");
        Header(ws, "Description", "Nil Rated Supplies", "Exempted(other than nil rated/non GST supply)", "Non-GST Supplies");
        string[] kinds = { "Inter-State supplies to registered persons", "Intra-State supplies to registered persons",
                           "Inter-State supplies to unregistered persons", "Intra-State supplies to unregistered persons" };
        for (var k = 0; k < kinds.Length; k++)
        {
            ws.Cell(k + 2, 1).Value = kinds[k];
            ws.Cell(k + 2, 2).Value = 0;
            ws.Cell(k + 2, 3).Value = k == 3 ? exemptValue : 0;
            ws.Cell(k + 2, 4).Value = 0;
        }

        // ── hsn (Table 12, B2C) ──
        var hsn = billLines.Select(l => (l.Sac, l.Rate, l.Value, l.Qty)).Concat(noteLines.Select(l => (l.Sac, l.Rate, Value: -l.Value, Qty: 0)))
            .GroupBy(x => (x.Sac, x.Rate)).OrderBy(g => g.Key.Sac).ThenBy(g => g.Key.Rate)
            .Select(g => (g.Key.Sac, g.Key.Rate, Value: g.Sum(x => x.Value), Qty: g.Sum(x => x.Qty))).ToList();
        ws = wb.AddWorksheet("hsn(b2c)");
        Header(ws, "HSN", "Description", "UQC", "Total Quantity", "Total Value", "Rate", "Taxable Value",
            "Integrated Tax Amount", "Central Tax Amount", "State/UT Tax Amount", "Cess Amount");
        row = 2;
        decimal taxTotal = 0, taxableTotal = 0;
        foreach (var h in hsn)
        {
            var taxable = R2(Taxable(h.Value, h.Rate));
            var tax = R2(h.Value) - taxable;
            taxTotal += tax; taxableTotal += taxable;
            ws.Cell(row, 1).Value = h.Sac;
            ws.Cell(row, 2).Value = h.Sac == GstDefaults.DiagnosticSac ? "Human health and social care services" : "";
            ws.Cell(row, 3).Value = "NA";
            ws.Cell(row, 4).Value = h.Qty;
            ws.Cell(row, 5).Value = R2(h.Value);
            ws.Cell(row, 6).Value = h.Rate;
            ws.Cell(row, 7).Value = taxable;
            ws.Cell(row, 8).Value = 0;
            ws.Cell(row, 9).Value = R2(tax / 2);
            ws.Cell(row, 10).Value = R2(tax - R2(tax / 2));
            ws.Cell(row, 11).Value = 0;
            row++;
        }

        // ── docs (Table 13) ──
        ws = wb.AddWorksheet("docs");
        Header(ws, "Nature of Document", "Sr. No. From", "Sr. No. To", "Total Number", "Cancelled");
        row = 2;
        var series = bills.Where(b => Regex.IsMatch(b.InvoiceId ?? "", NewSeries)).ToList();
        var older = bills.Where(b => !Regex.IsMatch(b.InvoiceId ?? "", NewSeries)).ToList();
        void DocRow(string nature, IReadOnlyCollection<string> numbers, int cancelled)
        {
            if (numbers.Count == 0) return;
            var sorted = numbers.OrderBy(n => n, StringComparer.Ordinal).ToList();
            ws.Cell(row, 1).Value = nature;
            ws.Cell(row, 2).Value = sorted[0];
            ws.Cell(row, 3).Value = sorted[^1];
            ws.Cell(row, 4).Value = numbers.Count;
            ws.Cell(row, 5).Value = cancelled;
            row++;
        }
        DocRow("Invoices for outward supply", series.Select(b => b.InvoiceId).ToList(), series.Count(b => !Live(b.Status, b.DeletedAt)));
        DocRow("Invoices for outward supply (older numbering)", older.Select(b => b.InvoiceId).ToList(), older.Count(b => !Live(b.Status, b.DeletedAt)));
        DocRow("Credit Note", notes.Where(n => n.Kind == CreditNoteKinds.CreditNote).Select(n => n.Number).ToList(), 0);
        DocRow("Refund Voucher", notes.Where(n => n.Kind == CreditNoteKinds.RefundVoucher).Select(n => n.Number).ToList(), 0);

        // ── registers ──
        ws = wb.AddWorksheet("Bills");
        Header(ws, "Bill no.", "Date (IST)", "Patient", "Status", "Bill value", "Taxable value", "CGST", "SGST", "Exempt value");
        row = 2;
        foreach (var b in bills)
        {
            var live = Live(b.Status, b.DeletedAt);
            var lines = live ? Spread(b.Id, b.TotalAmount) : new List<Line>();
            var tax = lines.Where(l => l.Rate > 0).Sum(l => l.Value - Taxable(l.Value, l.Rate));
            ws.Cell(row, 1).Value = b.InvoiceId;
            ws.Cell(row, 2).Value = IstDateRange.ToIst(b.CreatedAt).Date;
            ws.Cell(row, 2).Style.DateFormat.Format = "dd-MM-yyyy";
            ws.Cell(row, 3).Value = b.PatientName ?? "";
            ws.Cell(row, 4).Value = b.DeletedAt != null ? "DELETED" : b.Status;
            ws.Cell(row, 5).Value = live ? R2(b.TotalAmount) : 0;
            ws.Cell(row, 6).Value = R2(lines.Sum(l => Taxable(l.Value, l.Rate)));
            ws.Cell(row, 7).Value = R2(tax / 2);
            ws.Cell(row, 8).Value = R2(tax - R2(tax / 2));
            ws.Cell(row, 9).Value = R2(lines.Where(l => l.Rate <= 0).Sum(l => l.Value));
            row++;
        }

        ws = wb.AddWorksheet("Credit notes");
        Header(ws, "Number", "Type", "Date (IST)", "Against bill", "Bill date (IST)", "Patient", "Value", "Taxable value", "GST",
            "Refunded", "Refund mode", "In this month's figures", "Reason");
        row = 2;
        foreach (var n in notes)
        {
            ws.Cell(row, 1).Value = n.Number;
            ws.Cell(row, 2).Value = n.Kind == CreditNoteKinds.CreditNote ? "Credit note" : "Refund voucher";
            ws.Cell(row, 3).Value = IstDateRange.ToIst(n.CreatedAt).Date;
            ws.Cell(row, 3).Style.DateFormat.Format = "dd-MM-yyyy";
            ws.Cell(row, 4).Value = n.InvoiceNumber;
            ws.Cell(row, 5).Value = IstDateRange.ToIst(n.InvoiceDate).Date;
            ws.Cell(row, 5).Style.DateFormat.Format = "dd-MM-yyyy";
            ws.Cell(row, 6).Value = n.PatientName ?? "";
            ws.Cell(row, 7).Value = n.Amount;
            ws.Cell(row, 8).Value = n.TaxableValue;
            ws.Cell(row, 9).Value = n.TaxAmount;
            ws.Cell(row, 10).Value = n.RefundedAmount;
            ws.Cell(row, 11).Value = n.RefundModes ?? "";
            ws.Cell(row, 12).Value = n.Kind != CreditNoteKinds.CreditNote ? "No (money only)"
                : thisMonthBills.Contains(n.InvoiceId) ? "Already in the bill's value" : "Deducted";
            ws.Cell(row, 13).Value = n.Reason;
            row++;
        }

        // ── Summary (first sheet) ──
        ws = wb.AddWorksheet("Summary", 1);
        var liveBills = bills.Where(b => Live(b.Status, b.DeletedAt)).ToList();
        var rows = new List<(string Label, XLCellValue Value)>
        {
            ("Centre", hospital?.HospitalName ?? ""),
            ("GSTIN", gstin.Length == 15 ? gstin : "Not registered — no GSTIN on the centre's profile"),
            ("Place of supply", pos),
            ("Return period", period),
            ("", ""),
            ("Bills issued", bills.Count),
            ("  of which cancelled / deleted", bills.Count - liveBills.Count),
            ("Value of live bills (₹)", R2(liveBills.Sum(b => b.TotalAmount))),
            ("Credit notes issued", notes.Count(n => n.Kind == CreditNoteKinds.CreditNote)),
            ("  value deducted here (earlier months' bills) (₹)", R2(nettedNotes.Sum(n => n.Amount))),
            ("Refund vouchers issued", notes.Count(n => n.Kind == CreditNoteKinds.RefundVoucher)),
            ("", ""),
            ("Taxable value (₹)", R2(taxableTotal - exemptValue)),
            ("CGST (₹)", R2(hsn.Sum(h => { var t = R2(h.Value) - R2(Taxable(h.Value, h.Rate)); return R2(t / 2); }))),
            ("SGST (₹)", R2(hsn.Sum(h => { var t = R2(h.Value) - R2(Taxable(h.Value, h.Rate)); return t - R2(t / 2); }))),
            ("Exempt supplies (₹)", exemptValue),
            ("", ""),
            ("Notes", "Bills belong to the month they were issued (IST). Prices include GST; tax is split out of each line after the bill's discount."),
            ("", "Patients are unregistered, so supplies are B2C, intra-state (CGST + SGST)."),
            ("", "A credit note against an earlier month's bill is deducted here; one against this month's bill is already in that bill's value."),
            ("", "Exporting a month again after its bills were changed gives the current figures, not what was filed."),
        };
        ws.Cell(1, 1).Value = "GSTR-1 working sheet";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        for (var i = 0; i < rows.Count; i++)
        {
            ws.Cell(i + 3, 1).Value = rows[i].Label;
            ws.Cell(i + 3, 2).Value = rows[i].Value;
        }
        ws.Column(1).Width = 46;
        ws.Column(2).Width = 60;

        foreach (var sheet in wb.Worksheets.Where(s => s.Name != "Summary")) { sheet.Columns().AdjustToContents(); foreach (var col in sheet.ColumnsUsed()) if (col.Width > 60) col.Width = 60; }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return new Gstr1File(ms.ToArray(), $"GSTR1_{period}_{(gstin.Length == 15 ? gstin : "unregistered")}.xlsx");
    }

    private static void Header(IXLWorksheet ws, params string[] names)
    {
        for (var i = 0; i < names.Length; i++)
        {
            var c = ws.Cell(1, i + 1);
            c.Value = names[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = XLColor.FromHtml("#E2E8F0");
        }
        ws.SheetView.FreezeRows(1);
    }
}

/// <summary>GST state codes (first two digits of a GSTIN) — same list as the web's gst.js.</summary>
public static class GstStates
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["01"] = "Jammu & Kashmir", ["02"] = "Himachal Pradesh", ["03"] = "Punjab", ["04"] = "Chandigarh", ["05"] = "Uttarakhand",
        ["06"] = "Haryana", ["07"] = "Delhi", ["08"] = "Rajasthan", ["09"] = "Uttar Pradesh", ["10"] = "Bihar", ["11"] = "Sikkim",
        ["12"] = "Arunachal Pradesh", ["13"] = "Nagaland", ["14"] = "Manipur", ["15"] = "Mizoram", ["16"] = "Tripura", ["17"] = "Meghalaya",
        ["18"] = "Assam", ["19"] = "West Bengal", ["20"] = "Jharkhand", ["21"] = "Odisha", ["22"] = "Chhattisgarh", ["23"] = "Madhya Pradesh",
        ["24"] = "Gujarat", ["26"] = "Dadra & Nagar Haveli and Daman & Diu", ["27"] = "Maharashtra", ["29"] = "Karnataka", ["30"] = "Goa",
        ["31"] = "Lakshadweep", ["32"] = "Kerala", ["33"] = "Tamil Nadu", ["34"] = "Puducherry", ["35"] = "Andaman & Nicobar Islands",
        ["36"] = "Telangana", ["37"] = "Andhra Pradesh", ["38"] = "Ladakh",
    };

    /// <summary>"10-Bihar" (the offline tool's format), or "" when the GSTIN is missing.</summary>
    public static string PlaceOfSupply(string? gstin)
    {
        var code = (gstin ?? string.Empty).Length >= 2 ? gstin![..2] : string.Empty;
        return Names.TryGetValue(code, out var n) ? $"{code}-{n}" : string.Empty;
    }
}
