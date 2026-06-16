using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointment;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace _1Rad.UnitTests.Features.Appointments;

public class UpdateAppointmentCommandHandlerTests : BaseHandlerTest
{
    private readonly UpdateAppointmentCommandHandler _handler;

    public UpdateAppointmentCommandHandlerTests()
    {
        _handler = new UpdateAppointmentCommandHandler(Context);
    }

    [Fact]
    public async Task Handle_WithExistingReferrerCaseMismatchAndSpaces_ResolvesCorrectlyAndSyncsCommission()
    {
        // Arrange
        var patientId = Guid.NewGuid();
        var patient = new Patient
        {
            PatientId = patientId,
            FullName = "John Patient",
            HospitalId = HospitalId,
            Mobile = "9876543210"
        };
        Context.Patients.Add(patient);

        var appointment = new Appointment
        {
            AppointmentId = Guid.NewGuid(),
            PatientId = patientId,
            PatientName = "John Patient",
            HospitalId = HospitalId,
            Service = "X-Ray Chest",
            Modality = "XRAY",
            DateTime = DateTime.UtcNow,
            Doctor = "Dr. Radiologist",
            ReferredBy = "Self"
        };
        Context.Appointments.Add(appointment);

        var referrer = new Referrer
        {
            ReferrerId = Guid.NewGuid(),
            Name = "Dr. Jane Smith",
            Contact = "9988776655",
            HospitalId = HospitalId
        };
        Context.Referrers.Add(referrer);

        // Pre-create commission
        var commission = new ReferralCommission
        {
            ReferrerId = Guid.NewGuid(),
            ReferrerName = "Self",
            Modality = "XRAY",
            CommissionAmount = 0m,
            Status = "UNPAID",
            TransactionDate = DateTime.UtcNow,
            HospitalId = HospitalId,
            AppointmentId = appointment.AppointmentId
        };
        Context.ReferralCommissions.Add(commission);
        await Context.SaveChangesAsync();

        var command = new UpdateAppointmentCommand(
            AppointmentId: appointment.AppointmentId,
            Service: "X-Ray Chest",
            Modality: "XRAY",
            DateTime: DateTime.UtcNow,
            Doctor: "Dr. Radiologist",
            Notes: "Testing Update Casing",
            ReferredBy: "   dr. jane smith  ",
            PatientName: "John Patient",
            Mobile: "9876543210",
            PatientAge: "30",
            Amount: 1000m,
            ReferralCutValue: 150m
        );

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.Success);

        var updatedCommission = await Context.ReferralCommissions
            .FirstOrDefaultAsync(c => c.AppointmentId == appointment.AppointmentId);
        Assert.NotNull(updatedCommission);
        Assert.Equal(referrer.ReferrerId, updatedCommission.ReferrerId);
        Assert.Equal("Dr. Jane Smith", updatedCommission.ReferrerName);
        Assert.Equal(150m, updatedCommission.CommissionAmount);
    }

    [Fact]
    public async Task Handle_WithNewReferrerName_AutoCreatesReferrerAndCreatesCommission()
    {
        // Arrange
        var patientId = Guid.NewGuid();
        var patient = new Patient
        {
            PatientId = patientId,
            FullName = "John Patient",
            HospitalId = HospitalId,
            Mobile = "9876543210"
        };
        Context.Patients.Add(patient);

        var appointment = new Appointment
        {
            AppointmentId = Guid.NewGuid(),
            PatientId = patientId,
            PatientName = "John Patient",
            HospitalId = HospitalId,
            Service = "X-Ray Chest",
            Modality = "XRAY",
            DateTime = DateTime.UtcNow,
            Doctor = "Dr. Radiologist",
            ReferredBy = "Self"
        };
        Context.Appointments.Add(appointment);
        await Context.SaveChangesAsync();

        var command = new UpdateAppointmentCommand(
            AppointmentId: appointment.AppointmentId,
            Service: "X-Ray Chest",
            Modality: "XRAY",
            DateTime: DateTime.UtcNow,
            Doctor: "Dr. Radiologist",
            Notes: "Testing Auto-Creation",
            ReferredBy: "Dr. Brand New",
            PatientName: "John Patient",
            Mobile: "9876543210",
            PatientAge: "30",
            Amount: 1000m,
            ReferralCutValue: 200m
        );

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.Success);

        // Check referrer auto-creation
        var newReferrer = await Context.Referrers
            .FirstOrDefaultAsync(r => r.Name == "Dr. Brand New" && r.HospitalId == HospitalId);
        Assert.NotNull(newReferrer);

        var newCommission = await Context.ReferralCommissions
            .FirstOrDefaultAsync(c => c.AppointmentId == appointment.AppointmentId);
        Assert.NotNull(newCommission);
        Assert.Equal(newReferrer.ReferrerId, newCommission.ReferrerId);
        Assert.Equal("Dr. Brand New", newCommission.ReferrerName);
        Assert.Equal(200m, newCommission.CommissionAmount);
    }
}
