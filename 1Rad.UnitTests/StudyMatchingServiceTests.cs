using _1Rad.Application.Common;
using _1Rad.Domain.Entities;
using _1Rad.Infrastructure.Services;
using FluentAssertions;

namespace _1Rad.UnitTests;

/// <summary>
/// Server-side reconciliation of appointment-free studies: accession→appointment,
/// MRN→patient, unique-name→patient, with ambiguity and the manual-override guard.
/// </summary>
public class StudyMatchingServiceTests : BaseHandlerTest
{
    private StudyMatchingService Service() => new(Context);

    private async Task<ImagingStudy> SeedStudyAsync(Action<ImagingStudy> mutate)
    {
        var study = new ImagingStudy { Id = Guid.NewGuid(), HospitalId = HospitalId };
        mutate(study);
        Context.ImagingStudies.Add(study);
        await Context.SaveChangesAsync();
        return study;
    }

    private async Task<Patient> SeedPatientAsync(string? mrn = null, string? name = null)
    {
        var patient = new Patient
        {
            PatientId = Guid.NewGuid(),
            HospitalId = HospitalId,
            // FullName and PatientIdentifier are required; the matching keys are
            // NameNormalized / PatientIdentifier, which we set to a unique
            // non-colliding default unless the test is exercising that rule.
            FullName = name ?? "UNNAMED",
            PatientIdentifier = mrn ?? $"AUTO-{Guid.NewGuid():N}",
            NameNormalized = name == null ? null : NameNormalizer.Normalize(name),
        };
        Context.Patients.Add(patient);
        await Context.SaveChangesAsync();
        return patient;
    }

    private async Task<Appointment> SeedAppointmentAsync(Guid patientId)
    {
        var appt = new Appointment
        {
            AppointmentId = Guid.NewGuid(),
            HospitalId = HospitalId,
            PatientId = patientId,
            PatientName = "ZZ",
        };
        Context.Appointments.Add(appt);
        await Context.SaveChangesAsync();
        return appt;
    }

    [Fact]
    public async Task Matches_accession_to_appointment_and_its_patient()
    {
        var patient = await SeedPatientAsync();
        var appt = await SeedAppointmentAsync(patient.PatientId);
        var study = await SeedStudyAsync(s => s.AccessionNumber = appt.AppointmentId.ToString());

        var linked = await Service().TryMatchAsync(study, CancellationToken.None);

        linked.Should().BeTrue();
        study.AppointmentId.Should().Be(appt.AppointmentId);
        study.PatientId.Should().Be(patient.PatientId);
        study.MatchStatus.Should().Be(ImagingStudyMatchStatus.AutoMatched);
    }

    [Fact]
    public async Task Matches_unique_mrn_to_patient()
    {
        var patient = await SeedPatientAsync(mrn: "MRN-001");
        var study = await SeedStudyAsync(s => s.DicomPatientId = "MRN-001");

        var linked = await Service().TryMatchAsync(study, CancellationToken.None);

        linked.Should().BeTrue();
        study.PatientId.Should().Be(patient.PatientId);
        study.AppointmentId.Should().BeNull();
        study.MatchStatus.Should().Be(ImagingStudyMatchStatus.AutoMatched);
    }

    [Fact]
    public async Task Ambiguous_mrn_is_left_unmatched()
    {
        await SeedPatientAsync(mrn: "DUP");
        await SeedPatientAsync(mrn: "DUP");
        var study = await SeedStudyAsync(s => s.DicomPatientId = "DUP");

        var linked = await Service().TryMatchAsync(study, CancellationToken.None);

        linked.Should().BeFalse();
        study.PatientId.Should().BeNull();
        study.MatchStatus.Should().Be(ImagingStudyMatchStatus.Unmatched);
    }

    [Fact]
    public async Task Matches_unique_normalized_name_ignoring_honorific()
    {
        var patient = await SeedPatientAsync(name: "John Doe");
        // Honorific + casing differ but normalize to the same key.
        var study = await SeedStudyAsync(s => s.PatientName = "Dr. JOHN DOE");

        var linked = await Service().TryMatchAsync(study, CancellationToken.None);

        linked.Should().BeTrue();
        study.PatientId.Should().Be(patient.PatientId);
    }

    [Fact]
    public async Task Ambiguous_name_is_left_unmatched()
    {
        await SeedPatientAsync(name: "John Doe");
        await SeedPatientAsync(name: "John Doe");
        var study = await SeedStudyAsync(s => s.PatientName = "John Doe");

        var linked = await Service().TryMatchAsync(study, CancellationToken.None);

        linked.Should().BeFalse();
        study.PatientId.Should().BeNull();
    }

    [Fact]
    public async Task Never_overrides_a_manual_assignment()
    {
        var patient = await SeedPatientAsync(mrn: "MRN-9");
        var study = await SeedStudyAsync(s =>
        {
            s.DicomPatientId = "MRN-9";
            s.MatchStatus = ImagingStudyMatchStatus.ManuallyAssigned;
        });

        var linked = await Service().TryMatchAsync(study, CancellationToken.None);

        linked.Should().BeFalse();
        study.PatientId.Should().BeNull(); // untouched
    }

    [Fact]
    public async Task No_identifiers_leaves_it_unmatched()
    {
        var study = await SeedStudyAsync(_ => { });

        var linked = await Service().TryMatchAsync(study, CancellationToken.None);

        linked.Should().BeFalse();
        study.MatchStatus.Should().Be(ImagingStudyMatchStatus.Unmatched);
    }
}
