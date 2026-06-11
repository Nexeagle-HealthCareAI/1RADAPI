using _1Rad.Application.Features.Reporting.Commands.SaveReport;
using _1Rad.Application.Features.Reporting.Queries.GetReport;
using _1Rad.Domain.Entities;
using FluentAssertions;

namespace _1Rad.UnitTests;

/// <summary>
/// Cloud PACS-only reporting: a DiagnosticReport can be written directly against
/// an ImagingStudy with no appointment. Covers the study-based upsert, the
/// "one report per study" key, the appointment-only status rollup being skipped,
/// tenant isolation, and the study-based read.
/// </summary>
public class SaveReportStudyBasedTests : BaseHandlerTest
{
    private SaveReportCommandHandler SaveHandler() => new(Context);
    private GetReportQueryHandler GetHandler() => new(Context);

    private async Task<ImagingStudy> SeedStudyAsync(Guid? hospitalId = null)
    {
        var study = new ImagingStudy
        {
            Id = Guid.NewGuid(),
            HospitalId = hospitalId ?? HospitalId,
            PatientName = "ZZ TEST",
            Status = ImagingStudyStatus.Ready,
            MatchStatus = ImagingStudyMatchStatus.Unmatched,
        };
        Context.ImagingStudies.Add(study);
        await Context.SaveChangesAsync();
        return study;
    }

    [Fact]
    public async Task Creates_study_report_when_none_exists()
    {
        var study = await SeedStudyAsync();

        var report = await SaveHandler().Handle(new SaveReportCommand
        {
            ImagingStudyId = study.Id,
            Findings = "Normal study.",
            Impression = "No abnormality.",
            ReportingMode = "Narrative",
        }, CancellationToken.None);

        report.ImagingStudyId.Should().Be(study.Id);
        report.AppointmentId.Should().BeNull();
        report.AppointmentServiceId.Should().BeNull();
        report.HospitalId.Should().Be(HospitalId);
        report.DoctorId.Should().Be(UserId);
    }

    [Fact]
    public async Task Upserts_one_report_per_study()
    {
        var study = await SeedStudyAsync();

        var first = await SaveHandler().Handle(new SaveReportCommand
        {
            ImagingStudyId = study.Id, Findings = "v1", ReportingMode = "Narrative",
        }, CancellationToken.None);

        var second = await SaveHandler().Handle(new SaveReportCommand
        {
            ImagingStudyId = study.Id, Findings = "v2", ReportingMode = "Narrative",
        }, CancellationToken.None);

        second.Id.Should().Be(first.Id);              // same row, updated
        second.Findings.Should().Be("v2");
        Context.DiagnosticReports.Count(r => r.ImagingStudyId == study.Id).Should().Be(1);
    }

    [Fact]
    public async Task Finalize_does_not_throw_without_an_appointment()
    {
        var study = await SeedStudyAsync();

        var report = await SaveHandler().Handle(new SaveReportCommand
        {
            ImagingStudyId = study.Id,
            Findings = "Final.",
            IsFinalized = true,
            ReportingMode = "Narrative",
        }, CancellationToken.None);

        // The appointment/service status rollup is appointment-only; a
        // study-based finalize must simply finalize the report.
        report.IsFinalized.Should().BeTrue();
        report.FinalizedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Rejects_a_study_from_another_hospital()
    {
        // Seeded under a different hospital → hidden by the tenant query filter,
        // so the handler can't find it and refuses to write.
        var foreign = await SeedStudyAsync(hospitalId: Guid.NewGuid());

        var act = () => SaveHandler().Handle(new SaveReportCommand
        {
            ImagingStudyId = foreign.Id, Findings = "x", ReportingMode = "Narrative",
        }, CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Requires_either_an_appointment_or_a_study()
    {
        var act = () => SaveHandler().Handle(new SaveReportCommand
        {
            Findings = "orphan", ReportingMode = "Narrative",
        }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetReport_returns_the_study_based_report()
    {
        var study = await SeedStudyAsync();
        await SaveHandler().Handle(new SaveReportCommand
        {
            ImagingStudyId = study.Id, Findings = "findings", Impression = "imp", ReportingMode = "Narrative",
        }, CancellationToken.None);

        var fetched = await GetHandler().Handle(new GetReportQuery
        {
            ImagingStudyId = study.Id,
        }, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.ImagingStudyId.Should().Be(study.Id);
        fetched.Findings.Should().Be("findings");
    }
}
