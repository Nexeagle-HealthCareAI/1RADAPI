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
}