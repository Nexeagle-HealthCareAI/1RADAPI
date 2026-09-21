using _1Rad.Application.Features.Appointments.Queries.GetAppointments;
using _1Rad.Domain.Entities;

namespace _1Rad.UnitTests.Features.Appointments;

public class GetAppointmentsQueryHandlerTests : BaseHandlerTest
{
    [Fact]
    public async Task Handle_WithStartDate_ReturnsOnlyAppointmentsOnOrAfterDate()
    {
        var cutoff = DateTime.UtcNow.Date;
        var olderPatientId = Guid.NewGuid();
        var upcomingPatientId = Guid.NewGuid();
        Context.Patients.AddRange(
            new Patient { PatientId = olderPatientId, HospitalId = HospitalId, FullName = "Older Patient" },
            new Patient { PatientId = upcomingPatientId, HospitalId = HospitalId, FullName = "Upcoming Patient" });
        var olderAppointment = new Appointment
        {
            AppointmentId = Guid.NewGuid(),
            HospitalId = HospitalId,
            PatientId = olderPatientId,
            PatientName = "Older Patient",
            DateTime = cutoff.AddDays(-1),
            Status = "SCHEDULED",
            Priority = "ROUTINE"
        };
        var upcomingAppointment = new Appointment
        {
            AppointmentId = Guid.NewGuid(),
            HospitalId = HospitalId,
            PatientId = upcomingPatientId,
            PatientName = "Upcoming Patient",
            DateTime = cutoff.AddDays(1),
            Status = "SCHEDULED",
            Priority = "ROUTINE"
        };
        Context.Appointments.AddRange(olderAppointment, upcomingAppointment);
        await Context.SaveChangesAsync();

        var handler = new GetAppointmentsQueryHandler(Context);

        var result = await handler.Handle(
            new GetAppointmentsQuery(StartDate: cutoff),
            CancellationToken.None);

        var appointment = Assert.Single(result.Items);
        Assert.Equal(upcomingAppointment.AppointmentId, appointment.AppointmentId);
    }

    private Appointment NewAppointment(DateTime dateTimeUtc, string status = "CONFIRMED")
    {
        var patientId = Guid.NewGuid();
        Context.Patients.Add(new Patient { PatientId = patientId, HospitalId = HospitalId, FullName = "P " + patientId.ToString()[..4] });
        var a = new Appointment
        {
            AppointmentId = Guid.NewGuid(),
            HospitalId = HospitalId,
            PatientId = patientId,
            PatientName = "P",
            DateTime = dateTimeUtc,
            Status = status,
            Priority = "ROUTINE"
        };
        Context.Appointments.Add(a);
        return a;
    }

    [Fact]
    public async Task Handle_WithStartAndEndDate_ReturnsOnlyThatIstDay()
    {
        var day = DateTime.UtcNow.Date;
        var before = NewAppointment(day.AddDays(-1).AddHours(12));
        var inside = NewAppointment(day.AddHours(6));      // 11:30 IST on `day`
        var after = NewAppointment(day.AddDays(1).AddHours(12));
        await Context.SaveChangesAsync();

        var result = await new GetAppointmentsQueryHandler(Context).Handle(
            new GetAppointmentsQuery(StartDate: day, EndDate: day), CancellationToken.None);

        var only = Assert.Single(result.Items);
        Assert.Equal(inside.AppointmentId, only.AppointmentId);
        Assert.DoesNotContain(result.Items, i => i.AppointmentId == before.AppointmentId || i.AppointmentId == after.AppointmentId);
    }

    [Fact]
    public async Task Handle_ActiveSince_KeepsRecentAndOlderUnfinalizedButDropsOldFinalized()
    {
        var recentFrom = DateTime.UtcNow.Date.AddDays(-60);
        var oldDelivered = NewAppointment(recentFrom.AddDays(-20), "DELIVERED");
        var oldCancelled = NewAppointment(recentFrom.AddDays(-20), "cancelled");
        var oldStillActive = NewAppointment(recentFrom.AddDays(-20), "SCANNED");
        var recentDelivered = NewAppointment(recentFrom.AddDays(5), "DELIVERED");
        await Context.SaveChangesAsync();

        var result = await new GetAppointmentsQueryHandler(Context).Handle(
            new GetAppointmentsQuery(ActiveSince: recentFrom), CancellationToken.None);

        var ids = result.Items.Select(i => i.AppointmentId).ToHashSet();
        Assert.Contains(oldStillActive.AppointmentId, ids);
        Assert.Contains(recentDelivered.AppointmentId, ids);
        Assert.DoesNotContain(oldDelivered.AppointmentId, ids);
        Assert.DoesNotContain(oldCancelled.AppointmentId, ids);
    }

    [Fact]
    public async Task Handle_AttachesLiveServiceLinesToEachRow()
    {
        var appt = NewAppointment(DateTime.UtcNow);
        Context.AppointmentServices.AddRange(
            new AppointmentService { AppointmentId = appt.AppointmentId, HospitalId = HospitalId, ServiceName = "CT BRAIN", Modality = "CT", Amount = 2000 },
            new AppointmentService { AppointmentId = appt.AppointmentId, HospitalId = HospitalId, ServiceName = "GONE", Modality = "CT", Amount = 1, DeletedAt = DateTime.UtcNow });
        await Context.SaveChangesAsync();

        var result = await new GetAppointmentsQueryHandler(Context).Handle(new GetAppointmentsQuery(), CancellationToken.None);

        var row = Assert.Single(result.Items);
        var line = Assert.Single(row.Services!);
        Assert.Equal("CT BRAIN", line.ServiceName);
    }

    [Fact]
    public async Task Handle_UpdatedAfter_IncludesAVisitWhoseOnlyChangeIsAServiceLine()
    {
        var changed = NewAppointment(DateTime.UtcNow);
        var untouched = NewAppointment(DateTime.UtcNow);
        var svc = new AppointmentService { AppointmentId = changed.AppointmentId, HospitalId = HospitalId, ServiceName = "MRI", Modality = "MRI", Amount = 5000 };
        Context.AppointmentServices.Add(svc);
        await Context.SaveChangesAsync();

        await Task.Delay(30);
        var since = DateTime.UtcNow;
        await Task.Delay(30);

        // Only the service line changes (e.g. a technician note) — the parent
        // Appointment row itself is not modified, so its own UpdatedAt stays put.
        svc.TechnicianComments = "patient anxious";
        await Context.SaveChangesAsync();

        var result = await new GetAppointmentsQueryHandler(Context).Handle(
            new GetAppointmentsQuery(UpdatedAfter: since, IncludeDeleted: true), CancellationToken.None);

        var only = Assert.Single(result.Items);
        Assert.Equal(changed.AppointmentId, only.AppointmentId);
        Assert.DoesNotContain(result.Items, i => i.AppointmentId == untouched.AppointmentId);
    }
}