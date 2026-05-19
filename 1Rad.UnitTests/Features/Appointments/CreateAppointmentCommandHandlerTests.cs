using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Features.Appointments.Commands.CreateAppointment;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace _1Rad.UnitTests.Features.Appointments;

public class CreateAppointmentCommandHandlerTests : BaseHandlerTest
{
    private readonly CreateAppointmentCommandHandler _handler;

    public CreateAppointmentCommandHandlerTests()
    {
        _handler = new CreateAppointmentCommandHandler(Context);
    }

    [Fact]
    public async Task Handle_WithExistingReferrerExactMatch_AssignsReferrerAndCreatesCommission()
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

        var referrer = new Referrer
        {
            ReferrerId = Guid.NewGuid(),
            Name = "Dr. John Doe",
            Contact = "9988776655",
            HospitalId = HospitalId
        };
        Context.Referrers.Add(referrer);

        // Enable auto-billing
        var hospital = await Context.Hospitals.FindAsync(HospitalId);
        if (hospital != null)
        {
            hospital.IsAutoBillingEnabled = true;
        }
        await Context.SaveChangesAsync();

        var command = new CreateAppointmentCommand(
            PatientId: patientId,
            Service: "X-Ray Chest",
            Modality: "XRAY",
            DateTime: DateTime.UtcNow,
            Type: "scheduled",
            Doctor: "Dr. Radiologist",
            ReferredBy: "Dr. John Doe",
            ReferredContact: "9988776655",
            Notes: "Testing",
            Amount: 1000m,
            ReferralCutValue: 150m
        );

        // Act
        var appointmentId = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotEqual(Guid.Empty, appointmentId);
        
        var updatedPatient = await Context.Patients.FindAsync(patientId);
        Assert.Equal(referrer.ReferrerId, updatedPatient.ReferrerId);

        var commission = await Context.ReferralCommissions
            .FirstOrDefaultAsync(c => c.AppointmentId == appointmentId);
        Assert.NotNull(commission);
        Assert.Equal(referrer.ReferrerId, commission.ReferrerId);
        Assert.Equal("Dr. John Doe", commission.ReferrerName);
        Assert.Equal(150m, commission.CommissionAmount);
    }

    [Fact]
    public async Task Handle_WithExistingReferrerCaseMismatchAndSpaces_ResolvesCorrectlyAndCreatesCommission()
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

        var referrer = new Referrer
        {
            ReferrerId = Guid.NewGuid(),
            Name = "Dr. John Doe",
            Contact = "9988776655",
            HospitalId = HospitalId
        };
        Context.Referrers.Add(referrer);

        // Enable auto-billing
        var hospital = await Context.Hospitals.FindAsync(HospitalId);
        if (hospital != null)
        {
            hospital.IsAutoBillingEnabled = true;
        }
        await Context.SaveChangesAsync();

        // Mismatched casing and padding spaces
        var command = new CreateAppointmentCommand(
            PatientId: patientId,
            Service: "X-Ray Chest",
            Modality: "XRAY",
            DateTime: DateTime.UtcNow,
            Type: "scheduled",
            Doctor: "Dr. Radiologist",
            ReferredBy: "  dr. john doe   ",
            ReferredContact: "9988776655",
            Notes: "Testing Casing",
            Amount: 1000m,
            ReferralCutValue: 150m
        );

        // Act
        var appointmentId = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotEqual(Guid.Empty, appointmentId);

        var updatedPatient = await Context.Patients.FindAsync(patientId);
        Assert.Equal(referrer.ReferrerId, updatedPatient.ReferrerId);

        var commission = await Context.ReferralCommissions
            .FirstOrDefaultAsync(c => c.AppointmentId == appointmentId);
        Assert.NotNull(commission);
        Assert.Equal(referrer.ReferrerId, commission.ReferrerId);
        Assert.Equal("Dr. John Doe", commission.ReferrerName);
        Assert.Equal(150m, commission.CommissionAmount);
    }

    [Fact]
    public async Task Handle_WithNewReferrerName_AutoCreatesReferrerAndAssignsCommission()
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

        // Enable auto-billing
        var hospital = await Context.Hospitals.FindAsync(HospitalId);
        if (hospital != null)
        {
            hospital.IsAutoBillingEnabled = true;
        }
        await Context.SaveChangesAsync();

        var command = new CreateAppointmentCommand(
            PatientId: patientId,
            Service: "X-Ray Chest",
            Modality: "XRAY",
            DateTime: DateTime.UtcNow,
            Type: "scheduled",
            Doctor: "Dr. Radiologist",
            ReferredBy: "Dr. New Referrer",
            ReferredContact: "+91 99887 76655",
            Notes: "Testing Auto-Creation",
            Amount: 1000m,
            ReferralCutValue: 200m
        );

        // Act
        var appointmentId = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotEqual(Guid.Empty, appointmentId);

        // Check if Referrer was created
        var newReferrer = await Context.Referrers
            .FirstOrDefaultAsync(r => r.Name == "Dr. New Referrer" && r.HospitalId == HospitalId);
        Assert.NotNull(newReferrer);
        Assert.Equal("9988776655", newReferrer.Contact); // Sanitized contact number

        var updatedPatient = await Context.Patients.FindAsync(patientId);
        Assert.Equal(newReferrer.ReferrerId, updatedPatient.ReferrerId);

        var commission = await Context.ReferralCommissions
            .FirstOrDefaultAsync(c => c.AppointmentId == appointmentId);
        Assert.NotNull(commission);
        Assert.Equal(newReferrer.ReferrerId, commission.ReferrerId);
        Assert.Equal("Dr. New Referrer", commission.ReferrerName);
        Assert.Equal(200m, commission.CommissionAmount);
    }

    [Fact]
    public async Task Handle_WithAutoBillingDisabledButExplicitCut_CreatesCommission()
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

        var referrer = new Referrer
        {
            ReferrerId = Guid.NewGuid(),
            Name = "Dr. John Doe",
            Contact = "9988776655",
            HospitalId = HospitalId
        };
        Context.Referrers.Add(referrer);

        // Disable auto-billing
        var hospital = await Context.Hospitals.FindAsync(HospitalId);
        if (hospital != null)
        {
            hospital.IsAutoBillingEnabled = false;
        }
        await Context.SaveChangesAsync();

        var command = new CreateAppointmentCommand(
            PatientId: patientId,
            Service: "X-Ray Chest",
            Modality: "XRAY",
            DateTime: DateTime.UtcNow,
            Type: "scheduled",
            Doctor: "Dr. Radiologist",
            ReferredBy: "Dr. John Doe",
            ReferredContact: "9988776655",
            Notes: "Testing explicitly set cut value",
            Amount: 1000m,
            ReferralCutValue: 150m
        );

        // Act
        var appointmentId = await _handler.Handle(command, CancellationToken.None);

        // Assert
        var commission = await Context.ReferralCommissions
            .FirstOrDefaultAsync(c => c.AppointmentId == appointmentId);
        Assert.NotNull(commission); // Commission should be created because ReferralCutValue > 0
        Assert.Equal(150m, commission.CommissionAmount);
    }
}
