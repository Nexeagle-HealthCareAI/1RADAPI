using System.Collections.Concurrent;
using _1Rad.Application.Features.Reporting.Commands.SaveReport;
using _1Rad.Application.Features.Reporting.Commands.FinalizeReport;
using _1Rad.Application.Features.Reporting.Commands.AddAddendum;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Enums;
using FluentAssertions;
using Moq;

namespace _1Rad.UnitTests;

/// <summary>
/// Who may write and sign (ReportSignOff): doctors only sign, a doctor can take
/// over and sign a technician's draft but not another doctor's, wrong passwords
/// lock sign-off for a while, and a report can't hang off another visit's service.
/// </summary>
public class ReportSignoffRulesTests : BaseHandlerTest
{
    private readonly Mock<IPasswordHasher> _hasher = new();
    private readonly FakeLimiter _limiter = new();
    private readonly Guid _technicianId = Guid.NewGuid();
    private readonly Guid _otherDoctorId = Guid.NewGuid();

    public ReportSignoffRulesTests()
    {
        _hasher.Setup(h => h.Verify(It.IsAny<string>(), It.IsAny<string>()))
               .Returns((string pwd, string _) => pwd == "correct");

        Context.Roles.AddRange(
            new Role { RoleId = 1, RoleName = "AdminDoctor" },
            new Role { RoleId = 3, RoleName = "Doctor" },
            new Role { RoleId = 4, RoleName = "Technician" },
            new Role { RoleId = 5, RoleName = "Receptionist" });
        Context.SaveChanges();

        AddUser(UserId, "Dr. Signing Doctor", 3);
        AddUser(_technicianId, "Tech Typist", 4);
        AddUser(_otherDoctorId, "Dr. Someone Else", 3);
        Context.SaveChanges();

        ActAs(UserId, 3);
    }

    private void AddUser(Guid id, string name, int roleId)
    {
        Context.Users.Add(new User { UserId = id, FullName = name, Email = $"{id:N}@example.com", PasswordHash = "HASH", Status = UserStatus.Active });
        var mapping = new UserHospitalMapping { UserId = id, HospitalId = HospitalId };
        mapping.Roles.Add(Context.Roles.Local.Single(r => r.RoleId == roleId));
        Context.UserHospitalMappings.Add(mapping);
    }

    private void ActAs(Guid userId, int roleId)
    {
        MockUserContext.Setup(x => x.UserId).Returns(userId);
        MockUserContext.Setup(x => x.RoleIds).Returns(new[] { roleId });
    }

    private SaveReportCommandHandler Save() => new(Context);
    private FinalizeReportCommandHandler Finalize() => new(Context, _hasher.Object, null, _limiter);
    private AddAddendumCommandHandler Addendum() => new(Context, _hasher.Object, _limiter);

    private async Task<ImagingStudy> DraftByAsync(Guid authorId, int roleId)
    {
        var study = new ImagingStudy
        {
            Id = Guid.NewGuid(), HospitalId = HospitalId, PatientName = "ZZ TEST",
            Status = ImagingStudyStatus.Ready, MatchStatus = ImagingStudyMatchStatus.Unmatched,
        };
        Context.ImagingStudies.Add(study);
        await Context.SaveChangesAsync();
        ActAs(authorId, roleId);
        await Save().Handle(new SaveReportCommand
        {
            ImagingStudyId = study.Id, Findings = "Liver normal.", Impression = "No abnormality.", ReportingMode = "Narrative",
        }, CancellationToken.None);
        return study;
    }

    private FinalizeReportCommand Sign(Guid studyId, string pwd = "correct") =>
        new() { ImagingStudyId = studyId, TargetStatus = ReportStatuses.Final, Password = pwd };

    [Fact]
    public async Task A_receptionist_cannot_sign()
    {
        var study = await DraftByAsync(UserId, 3);
        ActAs(UserId, 5);

        var act = () => Finalize().Handle(Sign(study.Id), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*Only a doctor*");
        Context.DiagnosticReports.Single(r => r.ImagingStudyId == study.Id).Status.Should().Be(ReportStatuses.Draft);
    }

    [Fact]
    public async Task A_technician_cannot_sign_their_own_draft()
    {
        var study = await DraftByAsync(_technicianId, 4);

        var act = () => Finalize().Handle(Sign(study.Id), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task A_doctor_can_edit_and_sign_a_technicians_draft()
    {
        var study = await DraftByAsync(_technicianId, 4);
        ActAs(UserId, 3);

        await Save().Handle(new SaveReportCommand
        {
            ImagingStudyId = study.Id, Findings = "Liver normal. Checked by the doctor.", Impression = "Normal.", ReportingMode = "Narrative",
        }, CancellationToken.None);
        var report = await Finalize().Handle(Sign(study.Id), CancellationToken.None);

        report.Status.Should().Be(ReportStatuses.Final);
        report.DoctorId.Should().Be(UserId);
        report.SignedByUserId.Should().Be(UserId);
    }

    [Fact]
    public async Task A_doctor_cannot_take_over_another_doctors_draft()
    {
        var study = await DraftByAsync(_otherDoctorId, 3);
        ActAs(UserId, 3);

        var save = () => Save().Handle(new SaveReportCommand
        {
            ImagingStudyId = study.Id, Findings = "Overwrite", Impression = "x", ReportingMode = "Narrative",
        }, CancellationToken.None);
        var sign = () => Finalize().Handle(Sign(study.Id), CancellationToken.None);

        await save.Should().ThrowAsync<UnauthorizedAccessException>();
        await sign.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Five_wrong_passwords_pause_sign_off_even_for_the_right_password()
    {
        var study = await DraftByAsync(UserId, 3);
        for (var i = 0; i < 5; i++)
        {
            var wrong = () => Finalize().Handle(Sign(study.Id, "wrong"), CancellationToken.None);
            await wrong.Should().ThrowAsync<UnauthorizedAccessException>();
        }

        var right = () => Finalize().Handle(Sign(study.Id, "correct"), CancellationToken.None);

        await right.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*Too many wrong passwords*");
        Context.DiagnosticReports.Single(r => r.ImagingStudyId == study.Id).Status.Should().Be(ReportStatuses.Draft);
    }

    [Fact]
    public async Task Correct_passwords_are_never_throttled()
    {
        for (var i = 0; i < 8; i++)
        {
            var study = await DraftByAsync(UserId, 3);
            var report = await Finalize().Handle(Sign(study.Id), CancellationToken.None);
            report.Status.Should().Be(ReportStatuses.Final);
        }
    }

    [Fact]
    public async Task A_receptionist_cannot_add_an_addendum()
    {
        var study = await DraftByAsync(UserId, 3);
        await Finalize().Handle(Sign(study.Id), CancellationToken.None);
        ActAs(UserId, 5);

        var act = () => Addendum().Handle(new AddAddendumCommand { ImagingStudyId = study.Id, Text = "x", Password = "correct" }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task A_report_cannot_use_another_visits_service()
    {
        var patient = new Patient { PatientId = Guid.NewGuid(), HospitalId = HospitalId, FullName = "ZZ TEST", PatientIdentifier = $"AUTO-{Guid.NewGuid():N}" };
        var visitA = new Appointment { AppointmentId = Guid.NewGuid(), HospitalId = HospitalId, PatientId = patient.PatientId, PatientName = "ZZ" };
        var visitB = new Appointment { AppointmentId = Guid.NewGuid(), HospitalId = HospitalId, PatientId = patient.PatientId, PatientName = "ZZ" };
        var serviceOfB = new AppointmentService { Id = Guid.NewGuid(), AppointmentId = visitB.AppointmentId, HospitalId = HospitalId, ServiceName = "CT Head", Modality = "CT" };
        Context.Patients.Add(patient);
        Context.Appointments.AddRange(visitA, visitB);
        Context.AppointmentServices.Add(serviceOfB);
        await Context.SaveChangesAsync();

        var act = () => Save().Handle(new SaveReportCommand
        {
            AppointmentId = visitA.AppointmentId.ToString(), AppointmentServiceId = serviceOfB.Id,
            Findings = "x", Impression = "y", ReportingMode = "Narrative",
        }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private async Task<(Appointment Visit, AppointmentService Usg, AppointmentService Ct)> TwoStudyVisitAsync()
    {
        var patient = new Patient { PatientId = Guid.NewGuid(), HospitalId = HospitalId, FullName = "ZZ TEST", PatientIdentifier = $"AUTO-{Guid.NewGuid():N}" };
        var visit = new Appointment { AppointmentId = Guid.NewGuid(), HospitalId = HospitalId, PatientId = patient.PatientId, PatientName = "ZZ" };
        var usg = new AppointmentService { Id = Guid.NewGuid(), AppointmentId = visit.AppointmentId, HospitalId = HospitalId, ServiceName = "USG Abdomen", Modality = "USG" };
        var ct = new AppointmentService { Id = Guid.NewGuid(), AppointmentId = visit.AppointmentId, HospitalId = HospitalId, ServiceName = "CT Head", Modality = "CT" };
        Context.Patients.Add(patient);
        Context.Appointments.Add(visit);
        Context.AppointmentServices.AddRange(usg, ct);
        await Context.SaveChangesAsync();
        return (visit, usg, ct);
    }

    [Fact]
    public async Task On_a_two_study_visit_signing_must_name_the_study()
    {
        var (visit, usg, _) = await TwoStudyVisitAsync();
        await Save().Handle(new SaveReportCommand
        {
            AppointmentId = visit.AppointmentId.ToString(), AppointmentServiceId = usg.Id,
            Findings = "Liver normal.", Impression = "Normal.", ReportingMode = "Narrative",
        }, CancellationToken.None);

        var visitLevel = () => Finalize().Handle(new FinalizeReportCommand
        {
            AppointmentId = visit.AppointmentId.ToString(), TargetStatus = ReportStatuses.Final, Password = "correct",
        }, CancellationToken.None);
        await visitLevel.Should().ThrowAsync<ArgumentException>().WithMessage("*more than one study*");

        var report = await Finalize().Handle(new FinalizeReportCommand
        {
            AppointmentId = visit.AppointmentId.ToString(), AppointmentServiceId = usg.Id, TargetStatus = ReportStatuses.Final, Password = "correct",
        }, CancellationToken.None);
        report.Status.Should().Be(ReportStatuses.Final);
    }

    [Fact]
    public async Task Each_study_on_a_visit_keeps_its_own_report()
    {
        var (visit, usg, ct) = await TwoStudyVisitAsync();
        foreach (var (svc, text) in new[] { (usg, "USG text"), (ct, "CT text") })
        {
            await Save().Handle(new SaveReportCommand
            {
                AppointmentId = visit.AppointmentId.ToString(), AppointmentServiceId = svc.Id,
                Findings = text, Impression = "x", ReportingMode = "Narrative",
            }, CancellationToken.None);
        }

        var rows = Context.DiagnosticReports.Where(r => r.AppointmentId == visit.AppointmentId).ToList();
        rows.Should().HaveCount(2);
        rows.Single(r => r.AppointmentServiceId == usg.Id).Findings.Should().Be("USG text");
        rows.Single(r => r.AppointmentServiceId == ct.Id).Findings.Should().Be("CT text");
    }

    private sealed class FakeLimiter : IDistributedRateLimiter
    {
        private readonly ConcurrentDictionary<string, int> _hits = new();

        public Task<(bool Allowed, TimeSpan RetryAfter)> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken ct)
        {
            var n = _hits.AddOrUpdate(key, 1, (_, c) => c + 1);
            return Task.FromResult((n <= limit, window));
        }

        public Task<int> PeekAsync(string key, TimeSpan window, CancellationToken ct) =>
            Task.FromResult(_hits.TryGetValue(key, out var c) ? c : 0);
    }
}
