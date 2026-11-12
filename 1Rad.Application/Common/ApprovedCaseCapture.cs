using System.Text.Json;
using System.Text.RegularExpressions;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Common;

/// <summary>
/// When a report is signed Final and the centre has opted in, keep a
/// de-identified copy with the study's image references and any AI draft —
/// the raw material for teaching the AI this centre's reporting. Best effort:
/// never blocks or fails a sign-off.
/// </summary>
public static class ApprovedCaseCapture
{
    public static async Task CaptureAsync(IApplicationDbContext db, DiagnosticReport report, CancellationToken ct)
    {
        var policy = await db.CentreAiPolicies.AsNoTracking().FirstOrDefaultAsync(ct);
        if (policy is not { CaptureApprovedCases: true }) return;
        if (await db.ApprovedReportCases.AnyAsync(c => c.DiagnosticReportId == report.Id, ct)) return;

        var phi = await PatientPhi.CollectAsync(db, report.AppointmentId, report.ImagingStudyId, ct);
        string Deid(string? s) => Regex.Replace(PhiRedactor.Redact(s ?? string.Empty, phi).Text, @"\[\[PHI\d+\]\]", "[REDACTED]");

        // Which study, which images.
        string? modality = null, studyName = null, studyUid = null;
        if (report.ImagingStudyId is Guid sid)
        {
            var s = await db.ImagingStudies.AsNoTracking().Where(x => x.Id == sid)
                .Select(x => new { x.Modality, x.StudyDescription, x.StudyInstanceUID }).FirstOrDefaultAsync(ct);
            (modality, studyName, studyUid) = (s?.Modality, s?.StudyDescription, s?.StudyInstanceUID);
        }
        else if (report.AppointmentId is Guid aid)
        {
            if (report.AppointmentServiceId is Guid svcId)
            {
                var svc = await db.AppointmentServices.AsNoTracking().Where(x => x.Id == svcId)
                    .Select(x => new { x.Modality, x.ServiceName }).FirstOrDefaultAsync(ct);
                (modality, studyName) = (svc?.Modality, svc?.ServiceName);
            }
            else
            {
                var a = await db.Appointments.AsNoTracking().Where(x => x.AppointmentId == aid)
                    .Select(x => new { x.Modality, x.Service }).FirstOrDefaultAsync(ct);
                (modality, studyName) = (a?.Modality, a?.Service);
            }
            studyUid = await db.ImagingStudies.AsNoTracking()
                .Where(x => x.AppointmentId == aid && (report.AppointmentServiceId == null || x.AppointmentServiceId == report.AppointmentServiceId))
                .Select(x => x.StudyInstanceUID).FirstOrDefaultAsync(ct);
        }

        var keyQuery = report.ImagingStudyId is Guid ksid
            ? db.ReportKeyImages.Where(k => k.ImagingStudyId == ksid)
            : db.ReportKeyImages.Where(k => k.AppointmentId == report.AppointmentId && k.AppointmentServiceId == report.AppointmentServiceId);
        var keys = await keyQuery.AsNoTracking().OrderBy(k => k.CreatedAt)
            .Select(k => new { series = k.SeriesInstanceUid, sop = k.SopInstanceUid, frame = k.FrameIndex })
            .ToListAsync(ct);

        var draftQuery = report.ImagingStudyId is Guid dsid
            ? db.ReportAiDrafts.Where(d => d.ImagingStudyId == dsid)
            : db.ReportAiDrafts.Where(d => d.AppointmentId == report.AppointmentId && d.AppointmentServiceId == report.AppointmentServiceId);
        var draft = await draftQuery.AsNoTracking()
            .OrderByDescending(d => d.Applied).ThenByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(ct);

        double? similarity = null; int? distance = null;
        if (draft != null)
        {
            var (dist, sim) = WordDiff(PlainText(draft.Findings), PlainText(report.Findings));
            distance = dist; similarity = Math.Round(sim, 4);
        }

        var row = new ApprovedReportCase
        {
            HospitalId = report.HospitalId,
            DiagnosticReportId = report.Id,
            Modality = Cap(modality, 20),
            StudyName = Cap(studyName, 200),
            StudyInstanceUid = Cap(studyUid, 64),
            KeyImagesJson = keys.Count == 0 ? null : JsonSerializer.Serialize(keys),
            FindingsDeidentified = Deid(report.Findings),
            ImpressionDeidentified = string.IsNullOrWhiteSpace(report.Impression) ? null : Deid(report.Impression),
            AiDraftId = draft?.Id,
            AiDraftSource = draft?.Source,
            DraftSimilarity = similarity,
            DraftEditDistance = distance,
            SignedContentHash = report.SignedContentHash ?? string.Empty,
            SignedAt = report.SignedAt ?? DateTime.UtcNow,
        };
        db.ApprovedReportCases.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch { db.Entry(row).State = EntityState.Detached; throw; }
    }

    public static string PlainText(string? html) =>
        Regex.Replace(Regex.Replace(html ?? string.Empty, "<[^>]+>", " "), @"\s+", " ").Trim();

    /// <summary>Word-level edit distance and similarity (1 = identical).</summary>
    public static (int Distance, double Similarity) WordDiff(string a, string b)
    {
        var x = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var y = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (x.Length > 4000) x = x[..4000];
        if (y.Length > 4000) y = y[..4000];
        var prev = new int[y.Length + 1];
        var cur = new int[y.Length + 1];
        for (int j = 0; j <= y.Length; j++) prev[j] = j;
        for (int i = 1; i <= x.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= y.Length; j++)
            {
                var cost = string.Equals(x[i - 1], y[j - 1], StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        var d = prev[y.Length];
        var max = Math.Max(x.Length, y.Length);
        return (d, max == 0 ? 1.0 : 1.0 - (double)d / max);
    }

    private static string? Cap(string? s, int n) => s == null ? null : (s.Length > n ? s[..n] : s);
}
