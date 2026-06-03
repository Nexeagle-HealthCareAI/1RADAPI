using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using ClosedXML.Excel;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.ExportReferralIntelligence;

public record ExportReferralIntelligenceQuery(
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    bool AllTime = false
) : IRequest<byte[]>;

public class ExportReferralIntelligenceQueryHandler : IRequestHandler<ExportReferralIntelligenceQuery, byte[]>
{
    private readonly IApplicationDbContext _context;

    public ExportReferralIntelligenceQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<byte[]> Handle(ExportReferralIntelligenceQuery request, CancellationToken cancellationToken)
    {
        var query = _context.Appointments
            .Include(a => a.Patient)
            .AsNoTracking()
            // Cancelled visits aren't referral business — keep them out of the
            // export so it matches the on-screen referral intelligence.
            .Where(a => a.Status != "CANCELLED");

        if (!request.AllTime)
        {
            if (request.StartDate.HasValue)
                query = query.Where(a => a.DateTime.Date >= request.StartDate.Value.Date);
            
            if (request.EndDate.HasValue)
                query = query.Where(a => a.DateTime.Date <= request.EndDate.Value.Date);
        }

        // Multi-service rollout (batch-5 fix). The export is now driven
        // off the AppointmentServices child table — one row per scan —
        // so a 3-service visit appears as 3 lines, each with its own
        // modality and service name. The referrer's commission for
        // that line is preferred when attached via AppointmentServiceId,
        // falling back to the legacy per-modality attribution for
        // pre-step-2 commission rows. Visits with no live service rows
        // (legacy pre-migration-57 row) still emit one row from the
        // parent scalars so historical data stays auditable.
        var apptHeaders = await query
            .OrderByDescending(a => a.DateTime)
            .Select(a => new
            {
                a.AppointmentId,
                Referrer = a.ReferredBy ?? "Direct / Walk-in",
                PatientName = a.Patient != null ? (a.Patient.FullName ?? "Unknown") : "Unknown",
                PatientID = a.DisplayId,
                ParentModality = a.Modality,
                ParentService = a.Service,
                Status = a.Status,
                Date = a.DateTime.ToString("yyyy-MM-dd HH:mm"),
                Mobile = a.Mobile
            })
            .ToListAsync(cancellationToken);

        var apptIds = apptHeaders.Select(a => a.AppointmentId).ToList();
        var serviceLines = apptIds.Count == 0
            ? new Dictionary<Guid, List<(string Service, string Modality)>>()
            : (await _context.AppointmentServices
                .AsNoTracking()
                .Where(s => apptIds.Contains(s.AppointmentId) && s.DeletedAt == null)
                .OrderBy(s => s.UpdatedAt)
                .Select(s => new { s.AppointmentId, s.ServiceName, s.Modality })
                .ToListAsync(cancellationToken))
                .GroupBy(s => s.AppointmentId)
                .ToDictionary(g => g.Key, g => g
                    .Select(x => (Service: x.ServiceName ?? string.Empty, Modality: x.Modality ?? string.Empty))
                    .ToList());

        var data = apptHeaders.SelectMany(a =>
            (serviceLines.TryGetValue(a.AppointmentId, out var lines) && lines.Count > 0)
                ? lines.Select(l => new
                {
                    a.Referrer,
                    a.PatientName,
                    a.PatientID,
                    Modality = l.Modality,
                    Service  = l.Service,
                    a.Status,
                    a.Date,
                    a.Mobile,
                })
                : new[] { new
                {
                    a.Referrer,
                    a.PatientName,
                    a.PatientID,
                    Modality = a.ParentModality,
                    Service  = a.ParentService,
                    a.Status,
                    a.Date,
                    a.Mobile,
                } }
        ).ToList();

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Referral Intelligence");

        // Headers
        worksheet.Cell(1, 1).Value = "REFERRER";
        worksheet.Cell(1, 2).Value = "PATIENT NAME";
        worksheet.Cell(1, 3).Value = "PATIENT ID";
        worksheet.Cell(1, 4).Value = "MODALITY";
        worksheet.Cell(1, 5).Value = "SERVICE";
        worksheet.Cell(1, 6).Value = "STATUS";
        worksheet.Cell(1, 7).Value = "CONTACT";
        worksheet.Cell(1, 8).Value = "DATE_LOGGED";

        // Styling Headers
        var headerRange = worksheet.Range(1, 1, 1, 8);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#0f52ba");
        headerRange.Style.Font.FontColor = XLColor.White;

        // Data Rows
        for (int i = 0; i < data.Count; i++)
        {
            var row = i + 2;
            var item = data[i];
            worksheet.Cell(row, 1).Value = item.Referrer.ToUpper();
            worksheet.Cell(row, 2).Value = item.PatientName.ToUpper();
            worksheet.Cell(row, 3).Value = item.PatientID;
            worksheet.Cell(row, 4).Value = item.Modality;
            worksheet.Cell(row, 5).Value = item.Service;
            worksheet.Cell(row, 6).Value = item.Status.ToUpper();
            worksheet.Cell(row, 7).Value = item.Mobile;
            worksheet.Cell(row, 8).Value = item.Date;
        }

        worksheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
