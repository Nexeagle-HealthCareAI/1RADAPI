using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
using _1Rad.Application.Features.Patients.Commands.CreatePatient;
using _1Rad.Application.Features.Patients.Commands.UpdatePatient;
using _1Rad.Application.Features.Referrers.Queries.GetPatientSourceBreakdown;
using _1Rad.Domain.Entities;
using Xunit;

namespace _1Rad.UnitTests.Features.Patients;

/// <summary>
/// "How did you hear about us": one list of channels, stored consistently, never overwritten by a
/// re-registration, and totalled in a report.
/// </summary>
public class PatientSourceTests : BaseHandlerTest
{
    private static readonly DateTime Day = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    // ── the list ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("camp", "Camp")]
    [InlineData("  CAMP  ", "Camp")]
    [InlineData("friend/family", "Friend / Family")]
    [InlineData("FRIEND  /  FAMILY", "Friend / Family")]
    [InlineData("walk-in", "Walk-in")]
    [InlineData("walkin", "Walk-in")]
    [InlineData("by doctor", "By Doctor")]
    [InlineData("online / google", "Online / Google")]
    public void Canonicalize_WritesTheCanonicalSpelling_ForAnyCasingOrSpacing(string typed, string stored) =>
        Assert.Equal(stored, PatientSources.Canonicalize(typed));

    [Fact]
    public void Canonicalize_KeepsAnythingItDoesNotRecognise_AsTyped_SoNothingIsThrownAway()
    {
        Assert.Equal("Radio jingle on FM", PatientSources.Canonicalize("  Radio jingle on FM "));
        Assert.Equal(string.Empty, PatientSources.Canonicalize(null));
        Assert.Equal(string.Empty, PatientSources.Canonicalize("   "));
    }

    [Theory]
    [InlineData("Facebook", "Social Media")]
    [InlineData("Instagram reel", "Social Media")]
    [InlineData("Health camp", "Camp")]
    [InlineData("old patient", "Previous Patient")]
    [InlineData("Newspaper ad", "Newspaper / Hoarding")]
    [InlineData("Google search", "Online / Google")]
    [InlineData("my relative", "Friend / Family")]
    [InlineData("referred by Dr Rao", "By Doctor")]
    [InlineData("Camp", "Camp")]
    public void Classify_UnderstandsTheLooseWordingOlderRowsCarry(string stored, string channel) =>
        Assert.Equal(channel, PatientSources.Classify(stored));

    [Theory]
    [InlineData("advice from a colleague")]   // contains the letters "ad" but not the word
    [InlineData("on the road")]
    [InlineData("Something else entirely")]
    public void Classify_PutsUnrecognisedText_UnderOther_NeverByAccidentalSubstring(string stored) =>
        Assert.Equal(PatientSources.Other, PatientSources.Classify(stored));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_ReportsABlankChannelAsNotRecorded(string? stored) =>
        Assert.Equal(PatientSources.NotRecorded, PatientSources.Classify(stored));

    // ── writes ──────────────────────────────────────────────────────────────────

    private static CreatePatientCommand Create(string source, Guid? referrerId = null, string name = "Asha Devi", string mobile = "9876543210") =>
        new(name, mobile, "30", "Female", "V", "B", "D", "Addr", source, referrerId);

    [Fact]
    public async Task RegisteringANewPatient_StoresTheCanonicalChannel()
    {
        var id = await new CreatePatientCommandHandler(Context).Handle(Create("facebook / instagram"), CancellationToken.None);
        Assert.Equal("facebook / instagram", Context.Patients.Single(p => p.PatientId == id).SourceOfInfo);   // unrecognised: kept as typed

        var id2 = await new CreatePatientCommandHandler(Context).Handle(Create("SOCIAL MEDIA", name: "Ravi Kumar", mobile: "9000000001"), CancellationToken.None);
        Assert.Equal("Social Media", Context.Patients.Single(p => p.PatientId == id2).SourceOfInfo);
    }

    [Fact]
    public async Task ReRegisteringAKnownPatient_DoesNotOverwriteHowTheyFirstHeard_NorClearTheirReferrerLink()
    {
        var doctor = new Referrer { ReferrerId = Guid.NewGuid(), HospitalId = HospitalId, Name = "DR A" };
        Context.Referrers.Add(doctor);
        await Context.SaveChangesAsync();
        var handler = new CreatePatientCommandHandler(Context);

        var id = await handler.Handle(Create("Camp", doctor.ReferrerId), CancellationToken.None);
        // Second registration: the desk answers "Previous Patient" and the form carries no referrer.
        var again = await handler.Handle(Create("Previous Patient", null), CancellationToken.None);

        Assert.Equal(id, again);
        var p = Context.Patients.Single(x => x.PatientId == id);
        Assert.Equal("Camp", p.SourceOfInfo);
        Assert.Equal(doctor.ReferrerId, p.ReferrerId);
    }

    [Fact]
    public async Task ReRegisteringAKnownPatient_FillsAChannelAndReferrerThatWereNeverRecorded()
    {
        var doctor = new Referrer { ReferrerId = Guid.NewGuid(), HospitalId = HospitalId, Name = "DR A" };
        Context.Referrers.Add(doctor);
        await Context.SaveChangesAsync();
        var handler = new CreatePatientCommandHandler(Context);

        var id = await handler.Handle(Create(""), CancellationToken.None);
        await handler.Handle(Create("camp", doctor.ReferrerId), CancellationToken.None);

        var p = Context.Patients.Single(x => x.PatientId == id);
        Assert.Equal("Camp", p.SourceOfInfo);
        Assert.Equal(doctor.ReferrerId, p.ReferrerId);
    }

    [Fact]
    public async Task EditingAPatient_IsTheExplicitWayToCorrectTheChannel()
    {
        var id = await new CreatePatientCommandHandler(Context).Handle(Create("Camp"), CancellationToken.None);

        var ok = await new UpdatePatientCommandHandler(Context).Handle(
            new UpdatePatientCommand(id, "Asha Devi", "9876543210", "30", "Female", "V", "B", "D", "Addr", "walk in"), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("Walk-in", Context.Patients.Single(x => x.PatientId == id).SourceOfInfo);
    }

    // ── the report ──────────────────────────────────────────────────────────────

    private Patient AddPatient(string? channel, Guid? referrerId = null)
    {
        var p = new Patient { PatientId = Guid.NewGuid(), HospitalId = HospitalId, FullName = "P" + Guid.NewGuid().ToString()[..4], SourceOfInfo = channel, ReferrerId = referrerId };
        Context.Patients.Add(p);
        return p;
    }

    private Appointment AddVisit(Patient p, string? referredBy, Guid? referrerId, DateTime whenUtc, string status = "CONFIRMED")
    {
        var a = new Appointment
        {
            AppointmentId = Guid.NewGuid(), HospitalId = HospitalId, PatientId = p.PatientId, PatientName = p.FullName,
            DateTime = whenUtc, Status = status, Priority = "ROUTINE", ReferredBy = referredBy, ReferrerId = referrerId,
            ArrivedAt = status == "CONFIRMED" ? whenUtc : null,
        };
        Context.Appointments.Add(a);
        return a;
    }

    private Task<PatientSourceBreakdownDto> Report(DateTime? from = null, DateTime? to = null) =>
        new GetPatientSourceBreakdownQueryHandler(Context, MockUserContext.Object)
            .Handle(new GetPatientSourceBreakdownQuery(from, to), CancellationToken.None);

    [Fact]
    public async Task TheReport_TotalsVisitsPatientsAndNewPatientsPerChannel_AndGroupsLooseWording()
    {
        var dr = new Referrer { ReferrerId = Guid.NewGuid(), HospitalId = HospitalId, Name = "DR A" };
        Context.Referrers.Add(dr);
        var camp1 = AddPatient("Camp");
        var camp2 = AddPatient("health camp");       // loose wording -> Camp
        var doc = AddPatient("By Doctor");
        var blank = AddPatient(null);
        AddVisit(camp1, "SELF", null, Day.AddHours(6));
        AddVisit(camp1, "SELF", null, Day.AddDays(1).AddHours(6));       // repeat visit, same patient
        AddVisit(camp2, "SELF", null, Day.AddHours(7));
        AddVisit(doc, "DR A", dr.ReferrerId, Day.AddHours(8));
        AddVisit(blank, null, null, Day.AddHours(9));
        await Context.SaveChangesAsync();

        var r = await Report();

        var camp = r.Rows.Single(x => x.Source == "Camp");
        Assert.Equal(3, camp.Visits);
        Assert.Equal(2, camp.Patients);
        Assert.Equal(2, camp.NewPatients);            // camp1's first visit and camp2's only visit
        Assert.Equal(0, camp.PartnerVisits);
        var byDoctor = r.Rows.Single(x => x.Source == "By Doctor");
        Assert.Equal(1, byDoctor.PartnerVisits);
        Assert.Equal(PatientSources.NotRecorded, r.Rows.Last().Source);   // data-quality line is always last
        Assert.Equal(5, r.TotalVisits);
        Assert.Equal(4, r.TotalPatients);
        Assert.Equal(4, r.TotalNewPatients);
    }

    [Fact]
    public async Task TheReport_CountsOnlyPatientsWhoArrived_AndAPatientSeenBeforeTheRangeIsNotNew()
    {
        var p = AddPatient("Camp");
        AddVisit(p, "SELF", null, Day.AddDays(-30).AddHours(6));                       // earlier visit, outside the range
        AddVisit(p, "SELF", null, Day.AddHours(6));                                    // in range: a repeat
        AddVisit(AddPatient("Camp"), "SELF", null, Day.AddHours(7), status: "BOOKED"); // never arrived
        await Context.SaveChangesAsync();

        var r = await Report(Day, Day);

        var camp = Assert.Single(r.Rows);
        Assert.Equal(1, camp.Visits);
        Assert.Equal(0, camp.NewPatients);
        Assert.Equal(1, r.TotalPatients);
    }
}
