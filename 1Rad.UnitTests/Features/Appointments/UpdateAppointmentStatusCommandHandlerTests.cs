using _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentStatus;
using _1Rad.Domain.Entities;

namespace _1Rad.UnitTests.Features.Appointments;

public class UpdateAppointmentStatusCommandHandlerTests : BaseHandlerTest
{
    [Fact]
    public async Task Handle_BackwardTransition_ReturnsNotAllowedWithoutChangingAppointment()
    {
        var appointment = new Appointment
        {
            AppointmentId = Guid.NewGuid(),
            HospitalId = HospitalId,
            PatientId = Guid.NewGuid(),
            PatientName = "Test Patient",
            Status = "REPORTED",
            ArrivedAt = DateTime.UtcNow
        };
        Context.Appointments.Add(appointment);
        await Context.SaveChangesAsync();

        var handler = new UpdateAppointmentStatusCommandHandler(Context);

        var result = await handler.Handle(
            new UpdateAppointmentStatusCommand(appointment.AppointmentId, "IN_PROGRESS"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.NotAllowed);
        Assert.Equal("REPORTED", (await Context.Appointments.FindAsync(appointment.AppointmentId))!.Status);
    }
}