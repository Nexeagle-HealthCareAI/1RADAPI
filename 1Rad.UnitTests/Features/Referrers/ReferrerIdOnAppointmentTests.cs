using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
using _1Rad.Application.Features.Appointments.Commands.ChangeReferrer;
using _1Rad.Application.Features.Appointments.Commands.CreateAppointment;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointment;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentStatus;
using _1Rad.Application.Features.Referrers.Commands.UpdateReferrer;
using _1Rad.Application.Features.Referrers.Queries.GetReferralIntelligence;
using _1Rad.Application.Features.Referrers.Queries.GetReferralMatrix;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace _1Rad.UnitTests.Features.Referrers;

/// <summary>
/// A visit carries its referring partner's ID, not only the partner's name. Renaming a partner
/// used to orphan every earlier visit (the old spelling no longer matched any partner record);
/// with the id, history stays with the partner, and the commission at arrival is credited to the
/// same partner the analytics report the visit under.
/// </summary>
public class ReferrerIdOnAppointmentTests : BaseHandlerTest
{
    private static readonly DateTime Day = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    private Referrer AddReferrer(string name, Guid? mergedInto = null)
    {
        var r = new Referrer { ReferrerId = Guid.NewGuid(), HospitalId = HospitalId, Name = name, MergedIntoId = mergedInto };
        Context.Referrers.Add(r);
        return r;
    }

    private Patient AddPatient(Guid? referrerId = null)
    {
        var p = new Patient { PatientId = Guid.NewGuid(), HospitalId = HospitalId, FullName = "P" + Guid.NewGuid().ToString()[..4], Mobile = "9876543210", ReferrerId = referrerId };
        Context.Patients.Add(p);
        return p;
    }

    private Appointment AddVisit(string? referredBy, Guid? referrerId, Guid? patientReferrerId = null, string status = "CONFIRMED", DateTime? whenUtc = null)
    {
        var patient = AddPatient(patientReferrerId);
        var a = new Appointment
        {
            AppointmentId = Guid.NewGuid(), HospitalId = HospitalId, PatientId = patient.PatientId, PatientName = patient.FullName,
            DateTime = whenUtc ?? Day.AddHours(6), Status = status, Priority = "ROUTINE",
            ReferredBy = referredBy, ReferrerId = referrerId,
            ArrivedAt = status == "CONFIRMED" ? (whenUtc ?? Day.AddHours(6)) : null,
        };
        Context.Appointments.Add(a);
        return a;
    }

    private Task<List<ReferrerIntelligenceDto>> Intelligence(Guid? referrerId = null) =>
        new GetReferralIntelligenceQueryHandler(Context, MockUserContext.Object)
            .Handle(new GetReferralIntelligenceQuery(null, null, referrerId), CancellationToken.None);

    // ── attribution: the id is the key ──────────────────────────────────────────

    [Fact]
    public void TheVisitsOwnReferrerId_BeatsAStaleNameAndAStalePatientLink()
    {
        var real = new ReferralAttribution.Entry(Guid.NewGuid(), null, "DR REAL", null, null, null);
        var other = new ReferralAttribution.Entry(Guid.NewGuid(), null, "DR OTHER", null, null, null);
        var a = new ReferralAttribution(new[] { real, other });

        var s = a.Attribute("DR OTHER", other.ReferrerId, real.ReferrerId);

        Assert.Equal(SourceKind.Partner, s.Kind);
        Assert.Equal(real.ReferrerId, s.RootId);
    }

    [Fact]
    public void AVisitIdThatIsNotInTheRegistry_IsIgnored_AndTheNameIsUsed()
    {
        var real = new ReferralAttribution.Entry(Guid.NewGuid(), null, "DR REAL", null, null, null);
        var a = new ReferralAttribution(new[] { real });

        Assert.Equal(real.ReferrerId, a.Attribute("DR REAL", null, Guid.NewGuid()).RootId);
        Assert.Equal(SourceKind.Unlinked, a.Attribute("DR GHOST", null, Guid.NewGuid()).Kind);
    }

    [Fact]
    public void AVisitIdOfAMergedDuplicate_RollsUpToThePrimary()
    {
        var primary = new ReferralAttribution.Entry(Guid.NewGuid(), null, "DR PRIMARY", null, null, null);
        var dup = new ReferralAttribution.Entry(Guid.NewGuid(), primary.ReferrerId, "DR DUP", null, null, null);
        var a = new ReferralAttribution(new[] { primary, dup });

        Assert.Equal(primary.ReferrerId, a.Attribute("DR DUP", null, dup.ReferrerId).RootId);
    }

    [Fact]
    public async Task ARenamedPartnerKeepsItsHistory_InSourceAnalyticsAndTheMatrix()
    {
        // The partner was booked as "DR OLD SPELLING" and later renamed; the visits still carry the old text.
        var dr = AddReferrer("DR NEW SPELLING");
        AddVisit("DR OLD SPELLING", dr.ReferrerId);
        AddVisit("DR OLD SPELLING", dr.ReferrerId);
        AddVisit("DR NEW SPELLING", dr.ReferrerId);
        await Context.SaveChangesAsync();

        var node = Assert.Single(await Intelligence());
        Assert.Equal("PARTNER", node.SourceKind);
        Assert.Equal("DR NEW SPELLING", node.Name);
        Assert.Equal(3, node.TotalPatients);

        var matrix = await new GetReferralMatrixQueryHandler(Context)
            .Handle(new GetReferralMatrixQuery("DAY", Day, 1), CancellationToken.None);
        var row = Assert.Single(matrix.Rows, r => r.Kind == "PARTNER");
        Assert.Equal(3, row.Total);
    }

    [Fact]
    public async Task TheSinglePartnerDrillDown_FindsVisitsThatCarryOnlyTheIdAfterARename()
    {
        var dr = AddReferrer("DR NEW SPELLING");
        var other = AddReferrer("DR OTHER");
        AddVisit("DR OLD SPELLING", dr.ReferrerId);
        AddVisit("DR OTHER", other.ReferrerId);
        await Context.SaveChangesAsync();

        var node = Assert.Single(await Intelligence(dr.ReferrerId));

        Assert.Equal(dr.ReferrerId, node.ReferrerId);
        Assert.Equal(1, node.TotalPatients);
    }

    // ── rename ──────────────────────────────────────────────────────────────────

    private UpdateReferrerCommand Rename(Referrer r, string newName) =>
        new(r.ReferrerId, newName, string.Empty, string.Empty);

    [Fact]
    public async Task Renaming_CarriesTheNewNameToVisitsAndCommissionRows_AndTouchesNobodyElse()
    {
        var dr = AddReferrer("DR TYPO");
        var other = AddReferrer("DR OTHER");
        var byId = AddVisit("DR TYPO", dr.ReferrerId);
        var legacy = AddVisit("dr typo", null);                 // predates the id column: text only
        var theirs = AddVisit("DR OTHER", other.ReferrerId);
        var commission = new ReferralCommission
        {
            ReferrerId = dr.ReferrerId, ReferrerName = "DR TYPO", HospitalId = HospitalId, AppointmentId = byId.AppointmentId,
            Modality = "CT", CommissionAmount = 100m, Status = "UNPAID", TransactionDate = Day,
        };
        Context.ReferralCommissions.Add(commission);
        await Context.SaveChangesAsync();

        var ok = await new UpdateReferrerCommandHandler(Context).Handle(Rename(dr, "  dr   correct "), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("DR CORRECT", Context.Referrers.Single(r => r.ReferrerId == dr.ReferrerId).Name);
        var v1 = Context.Appointments.Single(a => a.AppointmentId == byId.AppointmentId);
        Assert.Equal("DR CORRECT", v1.ReferredBy);
        Assert.Equal(dr.ReferrerId, v1.ReferrerId);
        // A text-only visit is re-linked by id at the same time, so a rename can't orphan it.
        var v2 = Context.Appointments.Single(a => a.AppointmentId == legacy.AppointmentId);
        Assert.Equal("DR CORRECT", v2.ReferredBy);
        Assert.Equal(dr.ReferrerId, v2.ReferrerId);
        Assert.Equal("DR CORRECT", Context.ReferralCommissions.Single(c => c.Id == commission.Id).ReferrerName);
        // Another partner is untouched.
        var v3 = Context.Appointments.Single(a => a.AppointmentId == theirs.AppointmentId);
        Assert.Equal("DR OTHER", v3.ReferredBy);
        Assert.Equal(other.ReferrerId, v3.ReferrerId);

        // And the analytics still show all of the partner's history under the new name.
        var nodes = await Intelligence();
        Assert.Equal(2, nodes.Single(n => n.ReferrerId == dr.ReferrerId).TotalPatients);
    }

    [Fact]
    public async Task Renaming_ToANameAnotherLivePartnerHas_IsRefusedWithAClearMessage_NotADatabaseError()
    {
        var a = AddReferrer("DR A");
        AddReferrer("DR B");
        await Context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            new UpdateReferrerCommandHandler(Context).Handle(Rename(a, "dr b"), CancellationToken.None));

        Assert.Contains("merge", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("DR A", Context.Referrers.Single(r => r.ReferrerId == a.ReferrerId).Name);
    }

    [Fact]
    public async Task Renaming_ToABlankName_IsRefused()
    {
        var a = AddReferrer("DR A");
        await Context.SaveChangesAsync();

        await Assert.ThrowsAsync<ValidationException>(() =>
            new UpdateReferrerCommandHandler(Context).Handle(Rename(a, "   "), CancellationToken.None));
    }

    [Fact]
    public async Task SavingAProfileWithoutChangingTheName_LeavesTheVisitsAlone()
    {
        var dr = AddReferrer("DR SAME");
        var visit = AddVisit("DR SAME", dr.ReferrerId);
        await Context.SaveChangesAsync();
        var stamp = Context.Appointments.Single(a => a.AppointmentId == visit.AppointmentId).UpdatedAt;

        await new UpdateReferrerCommandHandler(Context).Handle(
            new UpdateReferrerCommand(dr.ReferrerId, "dr same", string.Empty, "New address"), CancellationToken.None);

        Assert.Equal(stamp, Context.Appointments.Single(a => a.AppointmentId == visit.AppointmentId).UpdatedAt);
        Assert.Equal("New address", Context.Referrers.Single(r => r.ReferrerId == dr.ReferrerId).Address);
    }

    // ── every writer stamps the id ──────────────────────────────────────────────

    [Fact]
    public async Task Booking_StampsThePartnersId()
    {
        var patient = AddPatient();
        var dr = AddReferrer("DR BOOKED");
        await Context.SaveChangesAsync();

        var id = await new CreateAppointmentCommandHandler(Context).Handle(new CreateAppointmentCommand(
            PatientId: patient.PatientId, Service: "X-Ray", Modality: "XRAY", DateTime: DateTime.UtcNow.AddDays(1),
            Type: "scheduled", Doctor: "Dr R", ReferredBy: "dr booked", Amount: 500m), CancellationToken.None);

        var saved = Context.Appointments.Single(a => a.AppointmentId == id);
        Assert.Equal("DR BOOKED", saved.ReferredBy);
        Assert.Equal(dr.ReferrerId, saved.ReferrerId);
    }

    [Fact]
    public async Task Booking_WithABrandNewReferrer_StampsTheNewPartnersId()
    {
        var patient = AddPatient();
        await Context.SaveChangesAsync();

        var id = await new CreateAppointmentCommandHandler(Context).Handle(new CreateAppointmentCommand(
            PatientId: patient.PatientId, Service: "X-Ray", Modality: "XRAY", DateTime: DateTime.UtcNow.AddDays(1),
            Type: "scheduled", Doctor: "Dr R", ReferredBy: "dr fresh", Amount: 500m), CancellationToken.None);

        var created = Context.Referrers.Single(r => r.Name == "DR FRESH");
        Assert.Equal(created.ReferrerId, Context.Appointments.Single(a => a.AppointmentId == id).ReferrerId);
    }

    [Fact]
    public async Task ChangingTheReferrer_MovesTheVisitsId()
    {
        var a = AddReferrer("DR A");
        var visit = AddVisit("DR A", a.ReferrerId, patientReferrerId: a.ReferrerId, status: "BOOKED");
        await Context.SaveChangesAsync();

        await new ChangeReferrerCommandHandler(Context).Handle(
            new ChangeReferrerCommand { AppointmentId = visit.AppointmentId, NewReferrerName = "dr b" }, CancellationToken.None);

        var b = Context.Referrers.Single(r => r.Name == "DR B");
        var saved = Context.Appointments.Single(x => x.AppointmentId == visit.AppointmentId);
        Assert.Equal("DR B", saved.ReferredBy);
        Assert.Equal(b.ReferrerId, saved.ReferrerId);
    }

    private static UpdateAppointmentCommand Edit(Appointment a, string? referredBy) =>
        new(a.AppointmentId, "X-Ray", "XRAY", a.DateTime, "Dr R", ReferredBy: referredBy, PatientName: a.PatientName, Mobile: "9876543210", Amount: 500m);

    [Fact]
    public async Task EditingABookedVisitsReferrer_ToAnExistingPartner_StampsItsId_AndToSelf_ClearsIt()
    {
        var a = AddReferrer("DR A");
        var b = AddReferrer("DR B");
        var visit = AddVisit("DR A", a.ReferrerId, status: "BOOKED");
        await Context.SaveChangesAsync();
        var handler = new UpdateAppointmentCommandHandler(Context);

        await handler.Handle(Edit(visit, "dr b"), CancellationToken.None);
        Assert.Equal(b.ReferrerId, Context.Appointments.Single(x => x.AppointmentId == visit.AppointmentId).ReferrerId);

        await handler.Handle(Edit(visit, "Self"), CancellationToken.None);
        Assert.Null(Context.Appointments.Single(x => x.AppointmentId == visit.AppointmentId).ReferrerId);
    }

    [Fact]
    public async Task EditingAVisitThatPredatesTheColumn_WithoutChangingTheReferrer_GivesItTheId()
    {
        var a = AddReferrer("DR A");
        var visit = AddVisit("DR A", referrerId: null, status: "BOOKED");
        await Context.SaveChangesAsync();

        await new UpdateAppointmentCommandHandler(Context).Handle(Edit(visit, "DR A"), CancellationToken.None);

        Assert.Equal(a.ReferrerId, Context.Appointments.Single(x => x.AppointmentId == visit.AppointmentId).ReferrerId);
    }

    // ── arrival credits the partner the visit is reported under ─────────────────

    private async Task<Appointment> BookedVisitWithACut(string referredBy, Guid? referrerId, Guid? patientReferrerId)
    {
        var visit = AddVisit(referredBy, referrerId, patientReferrerId, status: "BOOKED", whenUtc: DateTime.UtcNow.AddHours(1));
        Context.AppointmentServices.Add(new AppointmentService
        {
            AppointmentId = visit.AppointmentId, HospitalId = HospitalId, ServiceName = "CT", Modality = "CT", Amount = 1000m, ReferralCutValue = 200m,
        });
        await Context.SaveChangesAsync();
        return visit;
    }

    [Fact]
    public async Task ArrivalCommission_GoesToTheVisitsPartner_NotToWhoeverThePatientWasFirstLinkedTo()
    {
        var first = AddReferrer("DR FIRST");      // the patient's original link
        var edited = AddReferrer("DR EDITED");    // the referrer on THIS visit
        var visit = await BookedVisitWithACut("DR EDITED", edited.ReferrerId, first.ReferrerId);

        await new UpdateAppointmentStatusCommandHandler(Context).Handle(
            new UpdateAppointmentStatusCommand(visit.AppointmentId, "CONFIRMED"), CancellationToken.None);

        var c = Assert.Single(Context.ReferralCommissions.Where(x => x.AppointmentId == visit.AppointmentId).ToList());
        Assert.Equal(edited.ReferrerId, c.ReferrerId);
        Assert.Equal("DR EDITED", c.ReferrerName);
    }

    [Fact]
    public async Task ArrivalOfAVisitWithoutAnId_ResolvesTheNamedPartner_AndStampsTheId()
    {
        var first = AddReferrer("DR FIRST");
        var named = AddReferrer("DR NAMED");
        var visit = await BookedVisitWithACut("DR NAMED", null, first.ReferrerId);

        await new UpdateAppointmentStatusCommandHandler(Context).Handle(
            new UpdateAppointmentStatusCommand(visit.AppointmentId, "CONFIRMED"), CancellationToken.None);

        Assert.Equal(named.ReferrerId, Assert.Single(Context.ReferralCommissions.Where(x => x.AppointmentId == visit.AppointmentId).ToList()).ReferrerId);
        Assert.Equal(named.ReferrerId, Context.Appointments.Single(x => x.AppointmentId == visit.AppointmentId).ReferrerId);
    }

    [Fact]
    public async Task ArrivalOfAVisitWhoseNameMatchesNoPartner_StillFallsBackToThePatientsLink()
    {
        var linked = AddReferrer("DR LINKED");
        var visit = await BookedVisitWithACut("DR TYPO", null, linked.ReferrerId);

        await new UpdateAppointmentStatusCommandHandler(Context).Handle(
            new UpdateAppointmentStatusCommand(visit.AppointmentId, "CONFIRMED"), CancellationToken.None);

        Assert.Equal(linked.ReferrerId, Assert.Single(Context.ReferralCommissions.Where(x => x.AppointmentId == visit.AppointmentId).ToList()).ReferrerId);
        Assert.Null(Context.Appointments.Single(x => x.AppointmentId == visit.AppointmentId).ReferrerId);
    }
}
