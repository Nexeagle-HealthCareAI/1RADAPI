using System;
using System.Collections.Generic;
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
        // modality and service name. Visits with no live service rows
        // (legacy pre-migration-57 row) still emit one row from the
        // parent scalars so historical data stays auditable.
        var apptHeaders = await query
            .OrderByDescending(a => a.DateTime)
            .Select(a => new
            {
                a.AppointmentId,
                a.ReferredBy,
                a.ReferrerId,
                PatientReferrerId = a.Patient != null ? a.Patient.ReferrerId : null,
                PatientName = a.Patient != null ? (a.Patient.FullName ?? "Unknown") : "Unknown",
                PatientID = a.DisplayId,
                ParentModality = a.Modality,
                ParentService = a.Service,
                a.Status,
                a.ArrivedAt,
                DateUtc = a.DateTime,
                a.Mobile,
            })
            .ToListAsync(cancellationToken);

        // Only ATTENDED visits (the patient arrived) — a booked-and-still-ahead or a
        // never-arrived visit is not referral business yet, exactly like Source Analytics
        // and the Volume Matrix. The export used to include every non-cancelled row
        // regardless of attendance, so it counted MORE visits per partner than the
        // screen it is downloaded from.
        var nowUtc = DateTime.UtcNow;
        var attended = apptHeaders
            .Where(a => AppointmentAttendance.Classify(a.Status, a.ArrivedAt, a.DateUtc, nowUtc) == AppointmentAttendance.Attended)
            .ToList();

        // The SAME source-resolution rule as Source Analytics / the Volume Matrix - the visit's own
        // ReferrerId first, then its ReferredBy name, then the patient's link; a merged duplicate
        // rolls up to its primary. Self and "no referrer recorded" get their canonical single label
        // instead of splitting into whatever text (or lack of it) happened to be on the visit - the
        // export used to conflate a blank ReferredBy and a literal "Self" into one ad-hoc bucket
        // while every other report kept them apart (or the reverse), so the same visit could be
        // labelled differently here than on screen.
        var attribution = new ReferralAttribution(
            (await _context.Referrers.AsNoTracking()
                .Select(r => new { r.ReferrerId, r.MergedIntoId, r.Name, r.Contact, r.Address, r.DeletedAt })
                .ToListAsync(cancellationToken))
            .Select(r => new ReferralAttribution.Entry(r.ReferrerId, r.MergedIntoId, r.Name, r.Contact, r.Address, r.DeletedAt)));

        var apptIds = attended.Select(a => a.AppointmentId).ToList();
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

        var data = attended.SelectMany(a =>
        {
            var referrer = attribution.Attribute(a.ReferredBy, a.PatientReferrerId, a.ReferrerId).DisplayName;
            var lines = serviceLines.TryGetValue(a.AppointmentId, out var l) ? l : new List<(string Service, string Modality)>();
            return (lines.Count > 0 ? lines : new List<(string Service, string Modality)> { (a.ParentService, a.ParentModality) })
                .Select(line => new
                {
                    Referrer = referrer,
                    a.PatientName,
                    a.PatientID,
                    Modality = line.Modality,
                    Service = line.Service,
                    a.Status,
                    Date = IstDateRange.ToIst(a.DateUtc).ToString("yyyy-MM-dd HH:mm"),
                    a.Mobile,
                });
        }).ToList();

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
