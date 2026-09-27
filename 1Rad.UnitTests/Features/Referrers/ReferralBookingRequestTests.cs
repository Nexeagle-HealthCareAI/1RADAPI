using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Features.Appointments.Commands.CreateAppointment;
using _1Rad.Application.Features.Referrers.Commands.DeclineBookingRequest;
using _1Rad.Application.Features.Referrers.Commands.SubmitReferralBookingRequest;
using _1Rad.Application.Features.Referrers.Queries.GetBookingRequests;
using _1Rad.Application.Features.Referrers.Queries.GetDoctorBookingRequests;
using _1Rad.Application.Features.Referrers.Queries.GetPublicServiceMenu;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using Xunit;

namespace _1Rad.UnitTests.Features.Referrers;

/// <summary>
/// A referring doctor asks the centre to book a patient from their portal link. It lands as a
/// PENDING request (never a real Appointment - see ReferralBookingRequest's doc comment for why),
/// the front desk sees it in their queue, and either books it for real (which links the two
/// records and flips the request to SCHEDULED) or declines it with a reason the doctor can see.
/// </summary>
public class ReferralBookingRequestTests : BaseHandlerTest
{
    private Referrer AddReferrer(string name = "DR A")
    {
        var r = new Referrer { ReferrerId = Guid.NewGuid(), HospitalId = HospitalId, Name = name, Contact = "9876500000" };
        Context.Referrers.Add(r);
        return r;
    }

    private Patient AddPatient()
    {
        var p = new Patient { PatientId = Guid.NewGuid(), HospitalId = HospitalId, FullName = "Existing Patient", Mobile = "9876543210" };
        Context.Patients.Add(p);
        return p;
    }

    // ── submitting ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubmittingARequest_LandsAsPending_ScopedToTheReferrersHospital()
    {
        var dr = AddReferrer();
        await Context.SaveChangesAsync();

        var id = await new SubmitReferralBookingRequestCommandHandler(Context).Handle(
            new SubmitReferralBookingRequestCommand(dr.ReferrerId, "Asha Devi", "9876543210", "30", "Female", "CT", "CT Head", null, "Please prioritise"),
            CancellationToken.None);

        var saved = Context.ReferralBookingRequests.Single(r => r.Id == id);
        Assert.Equal("PENDING", saved.Status);
        Assert.Equal(dr.ReferrerId, saved.ReferrerId);
        Assert.Equal(HospitalId, saved.HospitalId);
        Assert.Equal("Asha Devi", saved.PatientName);
        Assert.Equal("9876543210", saved.Mobile);
    }

    [Fact]
    public async Task SubmittingWithoutAPatientName_IsRejected()
    {
        var dr = AddReferrer();
        await Context.SaveChangesAsync();

        await Assert.ThrowsAsync<ValidationException>(() => new SubmitReferralBookingRequestCommandHandler(Context).Handle(
            new SubmitReferralBookingRequestCommand(dr.ReferrerId, "  ", null, null, null, null, null, null, null),
            CancellationToken.None));
    }

    [Fact]
    public async Task SubmittingForAnUnknownReferrer_IsRejected()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => new SubmitReferralBookingRequestCommandHandler(Context).Handle(
            new SubmitReferralBookingRequestCommand(Guid.NewGuid(), "Asha Devi", null, null, null, null, null, null, null),
            CancellationToken.None));
    }

    [Theory]
    [InlineData("PatientName", 256)]
    [InlineData("Mobile", 21)]
    [InlineData("Age", 21)]
    [InlineData("Gender", 21)]
    [InlineData("Modality", 51)]
    [InlineData("ServiceName", 256)]
    [InlineData("Notes", 1001)]
    public async Task AnOverLongField_IsRefusedWithAMessage_AndNothingIsQueued(string field, int length)
    {
        var dr = AddReferrer();
        await Context.SaveChangesAsync();

        // A digit string for the phone, plain text for everything else.
        var tooLong = field == "Mobile" ? new string('9', length) : new string('x', length);
        var command = new SubmitReferralBookingRequestCommand(
            dr.ReferrerId,
            field == "PatientName" ? tooLong : "Asha Devi",
            field == "Mobile" ? tooLong : null,
            field == "Age" ? tooLong : null,
            field == "Gender" ? tooLong : null,
            field == "Modality" ? tooLong : null,
            field == "ServiceName" ? tooLong : null,
            null,
            field == "Notes" ? tooLong : null);

        await Assert.ThrowsAsync<ValidationException>(() =>
            new SubmitReferralBookingRequestCommandHandler(Context).Handle(command, CancellationToken.None));
        Assert.Empty(Context.ReferralBookingRequests);
    }

    [Fact]
    public async Task ValuesExactlyAtTheLimit_AreAccepted()
    {
        var dr = AddReferrer();
        await Context.SaveChangesAsync();

        var id = await new SubmitReferralBookingRequestCommandHandler(Context).Handle(
            new SubmitReferralBookingRequestCommand(
                dr.ReferrerId, new string('n', 255), new string('9', 20), new string('a', 20), new string('g', 20),
                new string('m', 50), new string('s', 255), null, new string('x', 1000)),
            CancellationToken.None);

        var saved = Context.ReferralBookingRequests.Single(r => r.Id == id);
        Assert.Equal(1000, saved.Notes!.Length);
        Assert.Equal(255, saved.PatientName.Length);
    }

    [Fact]
    public async Task APastPreferredDate_IsDropped_RatherThanTrusted()
    {
        var dr = AddReferrer();
        await Context.SaveChangesAsync();

        var id = await new SubmitReferralBookingRequestCommandHandler(Context).Handle(
            new SubmitReferralBookingRequestCommand(dr.ReferrerId, "Asha Devi", null, null, null, null, null, DateTime.UtcNow.AddDays(-5), null),
            CancellationToken.None);

        Assert.Null(Context.ReferralBookingRequests.Single(r => r.Id == id).PreferredDate);
    }

    [Fact]
    public async Task TheServiceMenu_NeverCarriesPriceOrCommissionData()
    {
        var dr = AddReferrer();
        Context.ServiceCharges.Add(new ServiceCharge { Id = Guid.NewGuid(), HospitalId = HospitalId, Modality = "CT", ServiceName = "CT Head", Amount = 2500m, ReferralCutValue = 500m });
        await Context.SaveChangesAsync();

        var menu = await new GetPublicServiceMenuQueryHandler(Context).Handle(new GetPublicServiceMenuQuery(dr.ReferrerId), CancellationToken.None);

        var row = Assert.Single(menu);
        Assert.Equal("CT", row.Modality);
        Assert.Equal("CT Head", row.ServiceName);
        // PublicServiceDto has no Amount/ReferralCutValue property at all - nothing to assert null on;
        // this test exists so a future field addition is a deliberate, reviewed choice.
        Assert.Equal(2, typeof(PublicServiceDto).GetProperties().Length);
    }

    // ── the doctor's own list ───────────────────────────────────────────────────

    [Fact]
    public async Task ADoctor_SeesOnlyTheirOwnRequests_NewestFirst()
    {
        var dr = AddReferrer();
        var other = AddReferrer("DR B");
        await Context.SaveChangesAsync();
        var handler = new SubmitReferralBookingRequestCommandHandler(Context);
        await handler.Handle(new SubmitReferralBookingRequestCommand(dr.ReferrerId, "First Patient", null, null, null, null, null, null, null), CancellationToken.None);
        await handler.Handle(new SubmitReferralBookingRequestCommand(dr.ReferrerId, "Second Patient", null, null, null, null, null, null, null), CancellationToken.None);
        await handler.Handle(new SubmitReferralBookingRequestCommand(other.ReferrerId, "Someone Else's Patient", null, null, null, null, null, null, null), CancellationToken.None);

        var mine = await new GetDoctorBookingRequestsQueryHandler(Context).Handle(new GetDoctorBookingRequestsQuery(dr.ReferrerId), CancellationToken.None);

        Assert.Equal(2, mine.Count);
        Assert.Equal("Second Patient", mine[0].PatientName);
        Assert.All(mine, r => Assert.Equal("PENDING", r.Status));
    }

    // ── the front desk's queue ───────────────────────────────────────────────────

    [Fact]
    public async Task TheQueue_ShowsPendingRequestsOldestFirst_WithTheReferrersName()
    {
        var dr = AddReferrer();
        await Context.SaveChangesAsync();
        var handler = new SubmitReferralBookingRequestCommandHandler(Context);
        await handler.Handle(new SubmitReferralBookingRequestCommand(dr.ReferrerId, "First In", null, null, null, null, null, null, null), CancellationToken.None);
        await Task.Delay(5);
        await handler.Handle(new SubmitReferralBookingRequestCommand(dr.ReferrerId, "Second In", null, null, null, null, null, null, null), CancellationToken.None);

        var queue = await new GetBookingRequestsQueryHandler(Context).Handle(new GetBookingRequestsQuery(), CancellationToken.None);

        Assert.Equal(2, queue.Count);
        Assert.Equal("First In", queue[0].PatientName);
        Assert.Equal("Second In", queue[1].PatientName);
        Assert.All(queue, r => Assert.Equal(dr.Name, r.ReferrerName));
    }

    [Fact]
    public async Task TheQueue_IsScopedToTheCallersHospital()
    {
        var dr = AddReferrer();
        await Context.SaveChangesAsync();
        await new SubmitReferralBookingRequestCommandHandler(Context).Handle(
            new SubmitReferralBookingRequestCommand(dr.ReferrerId, "In My Hospital", null, null, null, null, null, null, null), CancellationToken.None);
        // A request that belongs to some other hospital entirely.
        Context.ReferralBookingRequests.Add(new ReferralBookingRequest { Id = Guid.NewGuid(), HospitalId = Guid.NewGuid(), ReferrerId = Guid.NewGuid(), PatientName = "Elsewhere", Status = "PENDING" });
        await Context.SaveChangesAsync();

        var queue = await new GetBookingRequestsQueryHandler(Context).Handle(new GetBookingRequestsQuery(), CancellationToken.None);

        Assert.Single(queue);
        Assert.Equal("In My Hospital", queue[0].PatientName);
    }

    [Fact]
    public async Task ExcludingDecided_HidesEverythingButPending()
    {
        var dr = AddReferrer();
        await Context.SaveChangesAsync();
        var id = await new SubmitReferralBookingRequestCommandHandler(Context).Handle(
            new SubmitReferralBookingRequestCommand(dr.ReferrerId, "Patient", null, null, null, null, null, null, null), CancellationToken.None);
        await new DeclineBookingRequestCommandHandler(Context).Handle(new DeclineBookingRequestCommand(id, "duplicate"), CancellationToken.None);

        Assert.Empty(await new GetBookingRequestsQueryHandler(Context).Handle(new GetBookingRequestsQuery(IncludeDecided: false), CancellationToken.None));
        Assert.Single(await new GetBookingRequestsQueryHandler(Context).Handle(new GetBookingRequestsQuery(IncludeDecided: true), CancellationToken.None));
    }

    // ── declining ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Declining_RecordsTheReasonAndWhoDecidedIt_AndTheDoctorSeesIt()
    {
        var dr = AddReferrer();
        await Context.SaveChangesAsync();
        var id = await new SubmitReferralBookingRequestCommandHandler(Context).Handle(
            new SubmitReferralBookingRequestCommand(dr.ReferrerId, "Patient", null, null, null, null, null, null, null), CancellationToken.None);

        await new DeclineBookingRequestCommandHandler(Context).Handle(new DeclineBookingRequestCommand(id, "  No mobile number on file  "), CancellationToken.None);

        var saved = Context.ReferralBookingRequests.Single(r => r.Id == id);
        Assert.Equal("DECLINED", saved.Status);
        Assert.Equal("No mobile number on file", saved.DeclineReason);
        Assert.Equal(UserId, saved.DecidedByUserId);
        Assert.NotNull(saved.DecidedAt);

        var mine = await new GetDoctorBookingRequestsQueryHandler(Context).Handle(new GetDoctorBookingRequestsQuery(dr.ReferrerId), CancellationToken.None);
        Assert.Equal("DECLINED", Assert.Single(mine).Status);
        Assert.Equal("No mobile number on file", mine[0].DeclineReason);
    }

    [Fact]
    public async Task DecliningTwice_IsRejected_TheSecondTime()
    {
        var dr = AddReferrer();
        await Context.SaveChangesAsync();
        var id = await new SubmitReferralBookingRequestCommandHandler(Context).Handle(
            new SubmitReferralBookingRequestCommand(dr.ReferrerId, "Patient", null, null, null, null, null, null, null), CancellationToken.None);
        var handler = new DeclineBookingRequestCommandHandler(Context);
        await handler.Handle(new DeclineBookingRequestCommand(id, "first reason"), CancellationToken.None);

        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => handler.Handle(new DeclineBookingRequestCommand(id, "second reason"), CancellationToken.None));
    }

    [Fact]
    public async Task DecliningAnUnknownRequest_IsRejected()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => new DeclineBookingRequestCommandHandler(Context).Handle(
            new DeclineBookingRequestCommand(Guid.NewGuid(), null), CancellationToken.None));
    }

    // ── booking it for real ──────────────────────────────────────────────────────

    private static CreateAppointmentCommand Booking(Guid patientId, Guid? bookingRequestId) => new(
        PatientId: patientId, Service: "CT Head", Modality: "CT", DateTime: DateTime.UtcNow.AddDays(1),
        Type: "scheduled", Doctor: "Dr Lead Specialist", Amount: 2500m, ReferralCutValue: 500m,
        BookingRequestId: bookingRequestId);

    [Fact]
    public async Task BookingItForReal_MarksTheRequestScheduled_AndLinksTheAppointment()
    {
        var dr = AddReferrer();
        var patient = AddPatient();
        await Context.SaveChangesAsync();
        var requestId = await new SubmitReferralBookingRequestCommandHandler(Context).Handle(
            new SubmitReferralBookingRequestCommand(dr.ReferrerId, patient.FullName!, patient.Mobile, null, null, "CT", "CT Head", null, null), CancellationToken.None);

        var appointmentId = await new CreateAppointmentCommandHandler(Context).Handle(Booking(patient.PatientId, requestId), CancellationToken.None);

        var saved = Context.ReferralBookingRequests.Single(r => r.Id == requestId);
        Assert.Equal("SCHEDULED", saved.Status);
        Assert.Equal(appointmentId, saved.ResultingAppointmentId);
        Assert.Equal(UserId, saved.DecidedByUserId);
        var mine = Assert.Single(await new GetDoctorBookingRequestsQueryHandler(Context).Handle(new GetDoctorBookingRequestsQuery(dr.ReferrerId), CancellationToken.None));
        Assert.Equal("SCHEDULED", mine.Status);
    }

    [Fact]
    public async Task BookingWithoutARequestId_NeverTouchesTheQueue()
    {
        var patient = AddPatient();
        await Context.SaveChangesAsync();

        await new CreateAppointmentCommandHandler(Context).Handle(Booking(patient.PatientId, null), CancellationToken.None);

        Assert.Empty(Context.ReferralBookingRequests);
    }

    [Fact]
    public async Task BookingWithAnAlreadyDecidedRequestId_StillBooks_JustSkipsTheLinkBack()
    {
        var dr = AddReferrer();
        var patient = AddPatient();
        await Context.SaveChangesAsync();
        var requestId = await new SubmitReferralBookingRequestCommandHandler(Context).Handle(
            new SubmitReferralBookingRequestCommand(dr.ReferrerId, patient.FullName!, null, null, null, null, null, null, null), CancellationToken.None);
        await new DeclineBookingRequestCommandHandler(Context).Handle(new DeclineBookingRequestCommand(requestId, "already handled by phone"), CancellationToken.None);

        var appointmentId = await new CreateAppointmentCommandHandler(Context).Handle(Booking(patient.PatientId, requestId), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, appointmentId);
        Assert.Equal("DECLINED", Context.ReferralBookingRequests.Single(r => r.Id == requestId).Status);
    }
}
