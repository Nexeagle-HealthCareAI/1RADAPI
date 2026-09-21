using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
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
            .AsNoTracking()
            // Cancelled visits aren't referral business — keep them out of the
            // export so it matches the on-screen referral intelligence.
            .Where(a => a.Status != "CANCELLED");

        // The range arrives as bare "YYYY-MM-DD" IST days. Comparing the UTC .Date of a
        // stored timestamp against it shifted every day boundary by 5.5h, so the export
        // disagreed with the Referrals screen it is downloaded from (an evening visit
        // fell on the next day). Use the same IST day bounds as every other report.
        if (!request.AllTime)
        {
            if (request.StartDate.HasValue)
            {
                var fromUtc = IstDateRange.ToUtcStart(request.StartDate.Value);
                query = query.Where(a => a.DateTime >= fromUtc);
            }
            if (request.EndDate.HasValue)
            {
                var toUtc = IstDateRange.ToUtcEndInclusive(request.EndDate.Value);
                query = query.Where(a => a.DateTime <= toUtc);
            }
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
                ReferredBy = a.ReferredBy,
                PatientName = a.Patient != null ? (a.Patient.FullName ?? "Unknown") : "Unknown",
                PatientID = a.DisplayId,
                ParentModality = a.Modality,
                ParentService = a.Service,
                Status = a.Status,
                DateUtc = a.DateTime,
                Mobile = a.Mobile
            })
            .ToListAsync(cancellationToken);

        // A merged duplicate is reported under its primary partner's name, exactly as
        // the Referrals screen groups it — otherwise the same doctor is split across
        // several rows in the sheet.
        var registry = await _context.Referrers.AsNoTracking()
            .Select(r => new { r.ReferrerId, r.Name, r.MergedIntoId })
            .ToListAsync(cancellationToken);
        var byId = registry.ToDictionary(r => r.ReferrerId);
        var idByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in registry.OrderBy(r => r.MergedIntoId != null).ThenBy(r => r.ReferrerId))
            if (!string.IsNullOrWhiteSpace(r.Name)) idByName.TryAdd(r.Name!.Trim(), r.ReferrerId);
        string ReferrerLabel(string? referredBy)
        {
            var name = (referredBy ?? string.Empty).Trim();
            if (name.Length == 0) return "Direct / Walk-in";
            if (!idByName.TryGetValue(name, out var id)) return name;
            var seen = new HashSet<Guid>();
            while (byId.TryGetValue(id, out var node) && node.MergedIntoId.HasValue && seen.Add(id)) id = node.MergedIntoId.Value;
            return byId.TryGetValue(id, out var root) && !string.IsNullOrWhiteSpace(root.Name) ? root.Name! : name;
        }

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
                    Referrer = ReferrerLabel(a.ReferredBy),
                    a.PatientName,
                    a.PatientID,
                    Modality = l.Modality,
                    Service  = l.Service,
                    a.Status,
                    Date = IstDateRange.ToIst(a.DateUtc).ToString("yyyy-MM-dd HH:mm"),
                    a.Mobile,
                })
                : new[] { new
                {
                    Referrer = ReferrerLabel(a.ReferredBy),
                    a.PatientName,
                    a.PatientID,
                    Modality = a.ParentModality,
                    Service  = a.ParentService,
                    a.Status,
                    Date = IstDateRange.ToIst(a.DateUtc).ToString("yyyy-MM-dd HH:mm"),
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
