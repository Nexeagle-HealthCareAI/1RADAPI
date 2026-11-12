using _1Rad.Application.Common;
using _1Rad.Application.Features.Reporting.ImageLinks;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Infrastructure.Services;
using FluentAssertions;

namespace _1Rad.UnitTests;

/// <summary>
/// Scan-linked drafting groundwork: key images stick to the right report, the
/// image draft is off unless the centre switches it on and never sends the
/// patient's name, burned-in strips are blacked out, and Final reports are
/// kept de-identified (with the AI-draft comparison) only when opted in.
/// </summary>
public class ReportImageLinkTests : BaseHandlerTest
{
    private readonly FakeVisionClaude _claude = new();
    private readonly FakeRenderer _renderer = new();

    private async Task<(Appointment Visit, AppointmentService Svc)> VisitWithSliceAsync(string sop = "1.2.3.4.5")
    {
        var patient = new Patient { PatientId = Guid.NewGuid(), HospitalId = HospitalId, FullName = "Pooja Kumari", Age = "24", Gender = "Female", PatientIdentifier = "PT-1043" };
        var visit = new Appointment { AppointmentId = Guid.NewGuid(), HospitalId = HospitalId, PatientId = patient.PatientId, PatientName = "Pooja Kumari", Notes = "Pain abdomen, Pooja Kumari", Service = "USG Abdomen", Modality = "USG" };
        var svc = new AppointmentService { Id = Guid.NewGuid(), AppointmentId = visit.AppointmentId, HospitalId = HospitalId, ServiceName = "USG Abdomen", Modality = "USG" };
        var asset = new StudyAsset { Id = Guid.NewGuid(), HospitalId = HospitalId, AppointmentId = visit.AppointmentId, BlobUrl = "https://blob/x.zip", FileName = "x.zip", FileType = "zip" };
        Context.Patients.Add(patient);
        Context.Appointments.Add(visit);
        Context.AppointmentServices.Add(svc);
        Context.StudyAssets.Add(asset);
        Context.StudySliceIndexes.Add(new StudySliceIndex { AssetId = asset.Id, AppointmentId = visit.AppointmentId, HospitalId = HospitalId, SeriesUID = "1.2.3", SopInstanceUID = sop, Modality = "US", BlobUrl = "https://blob/s.dcm", BlobPath = "s.dcm" });
        await Context.SaveChangesAsync();
        return (visit, svc);
    }

    private static ReportOwner Owner(Appointment v, AppointmentService s) => new(v.AppointmentId, s.Id, null);

    [Fact]
    public async Task Key_images_are_kept_per_study_and_marking_twice_keeps_one()
    {
        var (visit, svc) = await VisitWithSliceAsync();
        var add = new AddKeyImageCommandHandler(Context);

        var a = await add.Handle(new AddKeyImageCommand(Owner(visit, svc), null, null, "1.2.3.4.5", null, 12, null), CancellationToken.None);
        var b = await add.Handle(new AddKeyImageCommand(Owner(visit, svc), null, null, "1.2.3.4.5", null, 12, null), CancellationToken.None);

        b.Id.Should().Be(a.Id);
        a.SeriesInstanceUid.Should().Be("1.2.3");      // filled from the slice index
        var list = await new GetKeyImagesQueryHandler(Context).Handle(new GetKeyImagesQuery(Owner(visit, svc)), CancellationToken.None);
        list.Should().HaveCount(1);
        var other = await new GetKeyImagesQueryHandler(Context).Handle(new GetKeyImagesQuery(new ReportOwner(visit.AppointmentId, Guid.NewGuid(), null)), CancellationToken.None);
        other.Should().BeEmpty();
    }

    [Fact]
    public async Task Drafting_from_images_is_off_by_default()
    {
        var (visit, svc) = await VisitWithSliceAsync();
        var res = await new VisionDraftCommandHandler(Context, _claude, _renderer, new ReportAiGate(Context))
            .Handle(new VisionDraftCommand(Owner(visit, svc)), CancellationToken.None);

        res.Success.Should().BeFalse();
        res.Error.Should().Contain("switched off");
        _claude.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Image_draft_sends_pictures_not_the_patients_name_and_is_kept()
    {
        var (visit, svc) = await VisitWithSliceAsync();
        Context.CentreAiPolicies.Add(new CentreAiPolicy { HospitalId = HospitalId, VisionDraftEnabled = true });
        await Context.SaveChangesAsync();
        await new AddKeyImageCommandHandler(Context).Handle(new AddKeyImageCommand(Owner(visit, svc), null, null, "1.2.3.4.5", null, 3, null), CancellationToken.None);
        _claude.Reply = "{\"findings_html\":\"<p>Liver normal.</p>\",\"impression\":\"Normal study.\"}";

        var res = await new VisionDraftCommandHandler(Context, _claude, _renderer, new ReportAiGate(Context))
            .Handle(new VisionDraftCommand(Owner(visit, svc)), CancellationToken.None);

        res.Success.Should().BeTrue();
        res.ImagesUsed.Should().Be(1);
        _claude.LastImages.Should().Be(1);
        _claude.LastUser.Should().NotContain("Pooja").And.NotContain("Kumari").And.Contain("USG Abdomen");
        Context.ReportAiDrafts.Single().Source.Should().Be("vision");
        Context.ReportAiUsageLogs.Single().Feature.Should().Be("vision");
    }

    [Fact]
    public void Burned_in_strips_are_blacked_out()
    {
        const int w = 10, h = 100;
        var px = Enumerable.Repeat((byte)200, w * h).ToArray();

        KeyImageRenderer.MaskStrips(px, rgb: false, w, h);

        px.Take(12 * w).Should().OnlyContain(b => b == 0);          // top 12%
        px.Skip((h - 7) * w).Should().OnlyContain(b => b == 0);     // bottom 7%
        px.Skip(50 * w).Take(w).Should().OnlyContain(b => b == 200); // middle untouched
    }

    [Fact]
    public void Draft_comparison_counts_word_changes()
    {
        var (d, sim) = ApprovedCaseCapture.WordDiff("liver is normal in size", "liver is mildly enlarged in size");
        d.Should().Be(2);
        sim.Should().BeApproximately(1 - 2.0 / 6, 0.001);
    }

    private async Task<DiagnosticReport> FinalReportAsync(Appointment visit, AppointmentService svc)
    {
        var r = new DiagnosticReport
        {
            Id = Guid.NewGuid(), HospitalId = HospitalId, AppointmentId = visit.AppointmentId, AppointmentServiceId = svc.Id,
            Findings = "<p>Pooja Kumari: liver is mildly enlarged in size.</p>", Impression = "Hepatomegaly.",
            Status = ReportStatuses.Final, IsFinalized = true, SignedAt = DateTime.UtcNow, SignedContentHash = "HASH",
        };
        Context.DiagnosticReports.Add(r);
        Context.ReportAiDrafts.Add(new ReportAiDraft { HospitalId = HospitalId, AppointmentId = visit.AppointmentId, AppointmentServiceId = svc.Id, Source = "vision", Findings = "<p>Liver is normal in size.</p>", Applied = true });
        await Context.SaveChangesAsync();
        return r;
    }

    [Fact]
    public async Task Final_reports_are_kept_only_when_the_centre_opted_in()
    {
        var (visit, svc) = await VisitWithSliceAsync();
        var report = await FinalReportAsync(visit, svc);

        await ApprovedCaseCapture.CaptureAsync(Context, report, CancellationToken.None);
        Context.ApprovedReportCases.Should().BeEmpty();

        Context.CentreAiPolicies.Add(new CentreAiPolicy { HospitalId = HospitalId, CaptureApprovedCases = true });
        await Context.SaveChangesAsync();
        await ApprovedCaseCapture.CaptureAsync(Context, report, CancellationToken.None);

        var kept = Context.ApprovedReportCases.Single();
        kept.FindingsDeidentified.Should().NotContain("Pooja").And.Contain("[REDACTED]").And.Contain("mildly enlarged");
        kept.AiDraftSource.Should().Be("vision");
        kept.DraftSimilarity.Should().BeLessThan(1.0);
        kept.Modality.Should().Be("USG");
    }

    private sealed class FakeRenderer : IKeyImageRenderer
    {
        public Task<RenderedKeyImage?> RenderAsync(string dicomBlobUrl, int maxDim, CancellationToken ct) =>
            Task.FromResult<RenderedKeyImage?>(new RenderedKeyImage(new byte[] { 0xFF, 0xD8, 0xFF }, "US", true));
    }

    private sealed class FakeVisionClaude : IAnthropicService
    {
        public string Reply { get; set; } = "{}";
        public string LastUser { get; private set; } = "";
        public int LastImages { get; private set; }
        public int Calls { get; private set; }
        public bool IsConfigured => true;
        public string ModelName => "test-vision";

        public Task<string> GenerateAsync(string s, string u, CancellationToken c = default) { Calls++; return Task.FromResult(Reply); }
        public Task<string> GenerateJsonAsync(string s, string u, object? r, CancellationToken c = default) { Calls++; return Task.FromResult(Reply); }
        public Task<AiJsonResult> GenerateJsonWithUsageAsync(string s, string u, object? r, CancellationToken c = default) { Calls++; return Task.FromResult(new AiJsonResult(Reply, new AiUsage(1, 1, 0, 0))); }
        public Task<AiJsonResult> GenerateJsonWithImagesAsync(string s, string u, IReadOnlyList<byte[]> images, CancellationToken c = default)
        {
            Calls++; LastUser = u; LastImages = images.Count;
            return Task.FromResult(new AiJsonResult(Reply, new AiUsage(1500, 200, 0, 0)));
        }
    }
}
