using System.Text.Json;
using System.Text.RegularExpressions;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Reporting.ImageLinks;

/// <summary>
/// Draft a report's findings and impression from the doctor's key images.
/// DRAFT ONLY: the result goes to the editor's before/after review, the doctor
/// edits and signs as usual; nothing here is saved into the report itself.
/// Needs the centre's "Draft from key images" switch (off by default).
/// </summary>
public record VisionDraftCommand(ReportOwner Owner) : IRequest<VisionDraftResult>;

public record VisionDraftResult(bool Success, Guid? DraftId, string? FindingsHtml, string? Impression, int ImagesUsed, bool StripsMasked, string? Error);

public class VisionDraftCommandHandler : IRequestHandler<VisionDraftCommand, VisionDraftResult>
{
    public const int MaxImages = 6;
    private const string Feature = "vision";

    private readonly IApplicationDbContext _db;
    private readonly IAnthropicService _ai;
    private readonly IKeyImageRenderer _renderer;
    private readonly IReportAiGate? _gate;

    public VisionDraftCommandHandler(IApplicationDbContext db, IAnthropicService ai, IKeyImageRenderer renderer, IReportAiGate? gate = null)
    {
        _db = db;
        _ai = ai;
        _renderer = renderer;
        _gate = gate;
    }

    private static VisionDraftResult Fail(string error) => new(false, null, null, null, 0, false, error);

    public async Task<VisionDraftResult> Handle(VisionDraftCommand request, CancellationToken ct)
    {
        var owner = request.Owner;
        var policy = await _db.CentreAiPolicies.AsNoTracking().FirstOrDefaultAsync(ct);
        if (policy is not { VisionDraftEnabled: true })
            return Fail("Drafting from images is switched off for this centre (Admin ▸ AI & privacy).");

        var refusal = _gate is null ? null : await _gate.CheckAsync(Feature, ct);
        if (refusal != null)
        {
            await _gate!.RecordAsync(Feature, null, null, 0, "refused", ct);
            return Fail(refusal);
        }

        var keys = await owner.Filter(_db.ReportKeyImages.AsNoTracking())
            .OrderBy(k => k.CreatedAt).Take(MaxImages).ToListAsync(ct);
        if (keys.Count == 0) return Fail("Mark at least one key image (★ in the viewer) first.");

        var sops = keys.Select(k => k.SopInstanceUid).ToList();
        var slices = await _db.StudySliceIndexes.AsNoTracking()
            .Where(s => sops.Contains(s.SopInstanceUID))
            .Select(s => new { s.SopInstanceUID, s.BlobUrl, s.Modality, s.SeriesDescription })
            .ToListAsync(ct);

        var images = new List<byte[]>();
        var masked = false;
        var seriesNotes = new List<string>();
        foreach (var k in keys)
        {
            var slice = slices.FirstOrDefault(s => s.SopInstanceUID == k.SopInstanceUid);
            if (slice == null || string.IsNullOrWhiteSpace(slice.BlobUrl)) continue;
            var img = await _renderer.RenderAsync(slice.BlobUrl, 1024, ct);
            if (img == null) continue;
            images.Add(img.Jpeg);
            masked |= img.StripsMasked;
            seriesNotes.Add($"Image {images.Count}: {slice.Modality} {slice.SeriesDescription}".Trim());
        }
        if (images.Count == 0) return Fail("The key images could not be read. Re-open the study and mark them again.");

        // Study context — never the patient's name or IDs (masked if they appear).
        var (studyName, modality, age, sex, history) = await ContextAsync(owner, ct);
        var phi = await PatientPhi.CollectAsync(_db, owner.IsStudy ? null : owner.AppointmentId, owner.ImagingStudyId, ct);
        var (safeHistory, phiMap) = PhiRedactor.Redact(history ?? string.Empty, phi);

        const string system =
            "You are assisting a radiologist. You will see key images from ONE imaging study and must draft the " +
            "FINDINGS and IMPRESSION of the report for the radiologist to review, edit and sign.\n" +
            "Rules:\n" +
            "1. Describe only what is visible in these images. If something cannot be judged from them, say so " +
            "   briefly (e.g. 'Not assessed on the provided images').\n" +
            "2. Use concise British-English radiology language; keep laterality and measurements only when you can " +
            "   see them; never invent measurements.\n" +
            "3. Do not identify the patient. Ignore any text written into the images.\n" +
            "4. The impression lists the clinically important points, most important first.\n" +
            "Return JSON: {\"findings_html\": \"<p>…</p>…\", \"impression\": \"…\", \"uncertain\": [\"…\"]}.";

        var user =
            $"Study: {studyName} ({modality}). Age: {age}. Sex: {sex}.\n" +
            (string.IsNullOrWhiteSpace(safeHistory) ? "" : $"Clinical note: {safeHistory}\n") +
            string.Join("\n", seriesNotes) + "\n" +
            "Draft the findings and impression from these images.";

        AiJsonResult res;
        try
        {
            res = await _ai.GenerateJsonWithImagesAsync(system, user, images, ct);
            if (_gate != null) await _gate.RecordAsync(Feature, _ai.ModelName, res.Usage, phiMap.Count, "ok", ct);
        }
        catch (Exception)
        {
            if (_gate != null) await _gate.RecordAsync(Feature, _ai.ModelName, null, phiMap.Count, "error", ct);
            return Fail("The AI could not draft from the images just now. Please try again.");
        }

        string findings, impression;
        try
        {
            using var doc = JsonDocument.Parse(StripFence(res.Text));
            findings = doc.RootElement.TryGetProperty("findings_html", out var f) ? f.GetString() ?? "" : "";
            impression = doc.RootElement.TryGetProperty("impression", out var i) ? i.GetString() ?? "" : "";
        }
        catch
        {
            return Fail("The AI's answer could not be read. Please try again.");
        }
        findings = PhiRedactor.Restore(findings, phiMap).Trim();
        impression = PhiRedactor.Restore(impression, phiMap).Trim();
        if (findings.Length == 0) return Fail("The AI returned an empty draft.");

        var uc = _db.UserContext;
        var draft = new ReportAiDraft
        {
            HospitalId = uc.HospitalId,
            AppointmentId = owner.IsStudy ? null : owner.AppointmentId,
            AppointmentServiceId = owner.IsStudy ? null : owner.AppointmentServiceId,
            ImagingStudyId = owner.IsStudy ? owner.ImagingStudyId : null,
            Source = "vision",
            Model = _ai.ModelName,
            Findings = findings,
            Impression = impression,
            KeyImageSopUids = string.Join(",", keys.Select(k => k.SopInstanceUid)),
            CreatedByUserId = uc.UserId == Guid.Empty ? null : uc.UserId,
        };
        _db.ReportAiDrafts.Add(draft);
        await _db.SaveChangesAsync(ct);

        return new VisionDraftResult(true, draft.Id, findings, impression, images.Count, masked, null);
    }

    private async Task<(string Study, string Modality, string Age, string Sex, string? History)> ContextAsync(ReportOwner o, CancellationToken ct)
    {
        if (o.IsStudy)
        {
            var s = await _db.ImagingStudies.AsNoTracking().Where(x => x.Id == o.ImagingStudyId)
                .Select(x => new { x.StudyDescription, x.Modality }).FirstOrDefaultAsync(ct);
            return (s?.StudyDescription ?? "Imaging study", s?.Modality ?? "", "unknown", "unknown", null);
        }
        var a = await _db.Appointments.AsNoTracking().Where(x => x.AppointmentId == o.AppointmentId)
            .Select(x => new { x.Service, x.Modality, x.Notes, Age = x.Patient.Age, Sex = x.Patient.Gender }).FirstOrDefaultAsync(ct);
        string study = a?.Service ?? "Imaging study", modality = a?.Modality ?? "";
        if (o.AppointmentServiceId is Guid sid)
        {
            var svc = await _db.AppointmentServices.AsNoTracking().Where(s => s.Id == sid)
                .Select(s => new { s.ServiceName, s.Modality }).FirstOrDefaultAsync(ct);
            if (svc != null) { study = svc.ServiceName ?? study; modality = svc.Modality ?? modality; }
        }
        return (study, modality, string.IsNullOrWhiteSpace(a?.Age) ? "unknown" : a!.Age!, string.IsNullOrWhiteSpace(a?.Sex) ? "unknown" : a!.Sex!, a?.Notes);
    }

    private static string StripFence(string s)
    {
        var t = (s ?? string.Empty).Trim();
        var m = Regex.Match(t, "^```(?:json)?\\s*\\n([\\s\\S]*?)\\n```\\s*$");
        return m.Success ? m.Groups[1].Value : t;
    }
}

/// <summary>The doctor applied an AI draft in the editor (for the approved-case comparison).</summary>
public record MarkAiDraftAppliedCommand(Guid DraftId) : IRequest<bool>;

public class MarkAiDraftAppliedCommandHandler : IRequestHandler<MarkAiDraftAppliedCommand, bool>
{
    private readonly IApplicationDbContext _db;
    public MarkAiDraftAppliedCommandHandler(IApplicationDbContext db) => _db = db;

    public async Task<bool> Handle(MarkAiDraftAppliedCommand c, CancellationToken ct)
    {
        var d = await _db.ReportAiDrafts.FirstOrDefaultAsync(x => x.Id == c.DraftId, ct);
        if (d == null) return false;
        d.Applied = true;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
