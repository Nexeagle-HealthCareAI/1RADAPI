using ClosedXML.Excel;
using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;

namespace _1Rad.Application.Features.Finance.Queries.ExportFinancials;

public record ExportFinancialsQuery : IRequest<byte[]>
{
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
}

public class ExportFinancialsQueryHandler : IRequestHandler<ExportFinancialsQuery, byte[]>
{
    private readonly IApplicationDbContext _context;

    public ExportFinancialsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<byte[]> Handle(ExportFinancialsQuery request, CancellationToken cancellationToken)
    {
        var invoicesQuery = _context.Invoices.AsQueryable();

        if (request.StartDate.HasValue)
            invoicesQuery = invoicesQuery.Where(i => i.CreatedAt >= request.StartDate.Value);

        if (request.EndDate.HasValue)
            invoicesQuery = invoicesQuery.Where(i => i.CreatedAt <= request.EndDate.Value);

        var invoices = await invoicesQuery
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(cancellationToken);

        using var workbook = new XLWorkbook();
        
        // --- SHEET 1: DAILY SUMMARY ---
        var dailySheet = workbook.Worksheets.Add("Daily Summary");
        var dailyData = invoices
            .GroupBy(i => i.CreatedAt.Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new
            {
                Date = g.Key.ToString("dd-MMM-yyyy"),
                Invoiced = g.Sum(i => i.TotalAmount),
                Collected = g.Sum(i => i.PaidAmount),
                Pending = g.Sum(i => i.TotalAmount - i.PaidAmount),
                Realization = g.Sum(i => i.TotalAmount) > 0 ? (g.Sum(i => i.PaidAmount) / g.Sum(i => i.TotalAmount)) * 100 : 0
            }).ToList();

        AddHeader(dailySheet, 1, "Date", "Invoiced", "Collected", "Pending", "Realization %");
        for (int i = 0; i < dailyData.Count; i++)
        {
            var d = dailyData[i];
            dailySheet.Cell(i + 2, 1).Value = d.Date;
            dailySheet.Cell(i + 2, 2).Value = d.Invoiced;
            dailySheet.Cell(i + 2, 3).Value = d.Collected;
            dailySheet.Cell(i + 2, 4).Value = d.Pending;
            dailySheet.Cell(i + 2, 5).Value = Math.Round(d.Realization, 2);
        }
        ApplyStyling(dailySheet, dailyData.Count + 1);

        // --- SHEET 2: MONTHLY SUMMARY ---
        var monthlySheet = workbook.Worksheets.Add("Monthly Summary");
        var monthlyData = invoices
            .GroupBy(i => new { i.CreatedAt.Year, i.CreatedAt.Month })
            .OrderByDescending(g => g.Key.Year).ThenByDescending(g => g.Key.Month)
            .Select(g => new
            {
                Month = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMMM yyyy"),
                Invoiced = g.Sum(i => i.TotalAmount),
                Collected = g.Sum(i => i.PaidAmount),
                Pending = g.Sum(i => i.TotalAmount - i.PaidAmount)
            }).ToList();

        AddHeader(monthlySheet, 1, "Month", "Invoiced", "Collected", "Pending");
        for (int i = 0; i < monthlyData.Count; i++)
        {
            var m = monthlyData[i];
            monthlySheet.Cell(i + 2, 1).Value = m.Month;
            monthlySheet.Cell(i + 2, 2).Value = m.Invoiced;
            monthlySheet.Cell(i + 2, 3).Value = m.Collected;
            monthlySheet.Cell(i + 2, 4).Value = m.Pending;
        }
        ApplyStyling(monthlySheet, monthlyData.Count + 1);

        // --- SHEET 3: RAW DATA (ALL INVOICES) ---
        var rawSheet = workbook.Worksheets.Add("Invoice Records");
        AddHeader(rawSheet, 1, "Invoice ID", "Date", "Patient", "Total", "Paid", "Status");
        for (int i = 0; i < invoices.Count; i++)
        {
            var inv = invoices[i];
            rawSheet.Cell(i + 2, 1).Value = inv.DisplayId;
            rawSheet.Cell(i + 2, 2).Value = inv.CreatedAt.ToString("dd-MMM-yyyy HH:mm");
            rawSheet.Cell(i + 2, 3).Value = inv.PatientName;
            rawSheet.Cell(i + 2, 4).Value = inv.TotalAmount;
            rawSheet.Cell(i + 2, 5).Value = inv.PaidAmount;
            rawSheet.Cell(i + 2, 6).Value = inv.Status;
        }
        ApplyStyling(rawSheet, invoices.Count + 1);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private void AddHeader(IXLWorksheet sheet, int row, params string[] headers)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = sheet.Cell(row, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#0f52ba");
            cell.Style.Font.FontColor = XLColor.White;
        }
    }

    private void ApplyStyling(IXLWorksheet sheet, int rows)
    {
        sheet.Columns().AdjustToContents();
        var range = sheet.Range(1, 1, rows, sheet.ColumnCount());
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
    }
}
