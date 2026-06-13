using _1Rad.Application.Features.Reporting.Commands.SaveReport;
using _1Rad.Application.Features.Reporting.Commands.FinalizeReport;
using _1Rad.Application.Features.Reporting.Commands.AddAddendum;
using _1Rad.Application.Common.Exceptions;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Enums;
using FluentAssertions;
using Moq;

namespace _1Rad.UnitTests;

/// <summary>
/// Electronic sign-off (21 CFR Part 11): the Draft → Preliminary → Final →
/// Addended state machine, password re-authentication, server-enforced
/// immutability, addendum-as-immutable-record, and the hash-chained audit trail.
/// </summary>
public class ReportSignoffTests : BaseHandlerTest
{
    private readonly Mock<IPasswordHasher> _hasher = new();

    public ReportSignoffTests()
    {
        // "correct" is the right password; anything else fails verification.
        _hasher.Setup(h => h.Verify(It.IsAny<string>(), It.IsAny<string>()))
               .Returns((string pwd, string _) => pwd == "correct");

        // Seed the signing user (matches the mocked UserContext.UserId).
        Context.Users.Add(new User
        {
            UserId = UserId,
            FullName = "Dr. Test Radiologist",
            Email = "doc@example.com",
            PasswordHash = "HASH",
            Degree = "MD",
            Status = UserStatus.Active,
        });
        Context.SaveChanges();
    }

    private SaveReportCommandHandler SaveHandler() => new(Context);
    private FinalizeReportCommandHandler FinalizeHandler() => new(Context, _hasher.Object);
    private AddAddendumCommandHandler AddendumHandler() => new(Context, _hasher.Object);

    private async Task<ImagingStudy> SeedStudyAsync()
    {
        var study = new ImagingStudy
        {
            Id = Guid.NewGuid(),
            HospitalId = HospitalId,
            PatientName = "ZZ TEST",
            Status = ImagingStudyStatus.Ready,
            MatchStatus = ImagingStudyMatchStatus.Unmatched,
        };
        Context.ImagingStudies.Add(study);
        await Context.SaveChangesAsync();
        return study;
    }

    private async Task<ImagingStudy> SeedDraftAsync()
    {
        var study = await SeedStudyAsync();
        await SaveHandler().Handle(new SaveReportCommand
        {
            ImagingStudyId = study.Id,
            Findings = "Liver normal.",
            Impression = "No abnormality.",
            ReportingMode = "Narrative",
        }, CancellationToken.None);
        return study;
    }

    [Fact]
    public async Task Finalize_with_correct_password_signs_and_locks()
    {
        var study = await SeedDraftAsync();

        var report = await FinalizeHandler().Handle(new FinalizeReportCommand
        {
            ImagingStudyId = study.Id, TargetStatus = ReportStatuses.Final, Password = "correct",
        }, CancellationToken.None);

        report.Status.Should().Be(ReportStatuses.Final);
        report.IsFinalized.Should().BeTrue();
        report.FinalizedAt.Should().NotBeNull();
        report.SignedAt.Should().NotBeNull();
        report.SignedByUserId.Should().Be(UserId);
        report.SignerName.Should().Be("Dr. Test Radiologist");
        report.SignerCredentials.Should().Be("MD");
        report.SignedContentHash.Should().NotBeNullOrEmpty();

        var events = Context.ReportAuditEvents.Where(e => e.ReportId == report.Id).ToList();
        events.Should().HaveCount(1);
        events[0].EventType.Should().Be(ReportAuditEventTypes.SignedFinal);
        events[0].ContentHash.Should().Be(report.SignedContentHash);
        events[0].PreviousHash.Should().BeNull();
    }

    [Fact]
    public async Task Finalize_with_wrong_password_is_rejected_and_changes_nothing()
    {
        var study = await SeedDraftAsync();

        var act = () => FinalizeHandler().Handle(new FinalizeReportCommand
        {
            ImagingStudyId = study.Id, TargetStatus = ReportStatuses.Final, Password = "wrong",
        }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();

        var report = Context.DiagnosticReports.Single(r => r.ImagingStudyId == study.Id);
        report.Status.Should().Be(ReportStatuses.Draft);
        report.IsFinalized.Should().BeFalse();
        Context.ReportAuditEvents.Count(e => e.ReportId == report.Id).Should().Be(0);
    }

    [Fact]
    public async Task Preliminary_then_Final_chains_the_audit_trail()
    {
        var study = await SeedDraftAsync();

        var prelim = await FinalizeHandler().Handle(new FinalizeReportCommand
        {
            ImagingStudyId = study.Id, TargetStatus = ReportStatuses.Preliminary, Password = "correct",
        }, CancellationToken.None);

        prelim.Status.Should().Be(ReportStatuses.Preliminary);
        prelim.IsFinalized.Should().BeFalse();   // a wet read isn't a closed report

        var final = await FinalizeHandler().Handle(new FinalizeReportCommand
        {
            ImagingStudyId = study.Id, TargetStatus = ReportStatuses.Final, Password = "correct",
        }, CancellationToken.None);

        final.Status.Should().Be(ReportStatuses.Final);
        final.IsFinalized.Should().BeTrue();

        var events = Context.ReportAuditEvents
            .Where(e => e.ReportId == final.Id).OrderBy(e => e.Timestamp).ToList();
        events.Should().HaveCount(2);
        events[0].EventType.Should().Be(ReportAuditEventTypes.SignedPreliminary);
        events[1].EventType.Should().Be(ReportAuditEventTypes.SignedFinal);
        events[1].PreviousHash.Should().Be(events[0].ContentHash);   // chained
    }

    [Fact]
    public async Task Re_signing_a_finalized_report_is_locked()
    {
        var study = await SeedDraftAsync();
        await FinalizeHandler().Handle(new FinalizeReportCommand
        {
            ImagingStudyId = study.Id, TargetStatus = ReportStatuses.Final, Password = "correct",
        }, CancellationToken.None);

        var act = () => FinalizeHandler().Handle(new FinalizeReportCommand
        {
            ImagingStudyId = study.Id, TargetStatus = ReportStatuses.Final, Password = "correct",
        }, CancellationToken.None);

        await act.Should().ThrowAsync<ReportLockedException>();
    }

    [Fact]
    public async Task Saving_a_finalized_report_is_locked()
    {
        var study = await SeedDraftAsync();
        await FinalizeHandler().Handle(new FinalizeReportCommand
        {
            ImagingStudyId = study.Id, TargetStatus = ReportStatuses.Final, Password = "correct",
        }, CancellationToken.None);

        var act = () => SaveHandler().Handle(new SaveReportCommand
        {
            ImagingStudyId = study.Id, Findings = "sneaky edit", ReportingMode = "Narrative",
        }, CancellationToken.None);

        await act.Should().ThrowAsync<ReportLockedException>();
    }

    [Fact]
    public async Task Saving_a_preliminary_report_keeps_it_editable_and_preliminary()
    {
        var study = await SeedDraftAsync();
        await FinalizeHandler().Handle(new FinalizeReportCommand
        {
            ImagingStudyId = study.Id, TargetStatus = ReportStatuses.Preliminary, Password = "correct",
        }, CancellationToken.None);

        // Editing a wet-read is allowed; the status must NOT silently change.
        var updated = await SaveHandler().Handle(new SaveReportCommand
        {
            ImagingStudyId = study.Id, Findings = "Liver normal. Spleen normal.", ReportingMode = "Narrative",
        }, CancellationToken.None);

        updated.Findings.Should().Be("Liver normal. Spleen normal.");
        updated.Status.Should().Be(ReportStatuses.Preliminary);
    }

    [Fact]
    public async Task Addendum_appends_an_immutable_record_without_touching_signed_content()
    {
        var study = await SeedDraftAsync();
        var final = await FinalizeHandler().Handle(new FinalizeReportCommand
        {
            ImagingStudyId = study.Id, TargetStatus = ReportStatuses.Final, Password = "correct",
        }, CancellationToken.None);
        var signedFindings = final.Findings;
        var signedHash = final.SignedContentHash;

        var report = await AddendumHandler().Handle(new AddAddendumCommand
        {
            ImagingStudyId = study.Id, Text = "On review, also note a small cyst.", Password = "correct",
        }, CancellationToken.None);

        report.Status.Should().Be(ReportStatuses.Addended);
        report.IsFinalized.Should().BeTrue();
        report.Findings.Should().Be(signedFindings);     // signed content untouched
        report.SignedContentHash.Should().Be(signedHash);

        var addenda = Context.ReportAddenda.Where(a => a.ReportId == report.Id).ToList();
        addenda.Should().HaveCount(1);
        addenda[0].Text.Should().Be("On review, also note a small cyst.");
        addenda[0].SortOrder.Should().Be(1);
        addenda[0].AuthorUserId.Should().Be(UserId);

        Context.ReportAuditEvents.Count(e => e.ReportId == report.Id
            && e.EventType == ReportAuditEventTypes.AddendumAdded).Should().Be(1);
    }

    [Fact]
    public async Task Addendum_on_a_draft_is_rejected()
    {
        var study = await SeedDraftAsync();

        var act = () => AddendumHandler().Handle(new AddAddendumCommand
        {
            ImagingStudyId = study.Id, Text = "too early", Password = "correct",
        }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Addendum_with_wrong_password_is_rejected()
    {
        var study = await SeedDraftAsync();
        await FinalizeHandler().Handle(new FinalizeReportCommand
        {
            ImagingStudyId = study.Id, TargetStatus = ReportStatuses.Final, Password = "correct",
        }, CancellationToken.None);

        var act = () => AddendumHandler().Handle(new AddAddendumCommand
        {
            ImagingStudyId = study.Id, Text = "x", Password = "nope",
        }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }
}
