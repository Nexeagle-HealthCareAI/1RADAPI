using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Features.Appointments;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointment;
using _1Rad.Application.Features.Finance.Queries.GetInvoices;
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
            ReferredBy = "Self",
            // Commissions are arrival-gated — the patient must have arrived for the
            // edit to reconcile referral commissions at all.
            ArrivedAt = DateTime.UtcNow,
            Status = "CONFIRMED"
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
            ReferredBy = "Self",
            // Commissions are arrival-gated — arrive the patient so the edit
            // reconciles (creates) the referral commission.
            ArrivedAt = DateTime.UtcNow,
            Status = "CONFIRMED"
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

    [Fact]
    public async Task Handle_ArrivedAppointmentEdit_ReconcilesPatientServicesAndRevenueInvoice()
    {
        var patientId = Guid.NewGuid();
        var patient = new Patient
        {
            PatientId = patientId,
            HospitalId = HospitalId,
            FullName = "Original Patient",
            Mobile = "9000000000",
            Age = "35",
            Gender = "Female",
            SourceOfInfo = "Walk-in"
        };
        var appointment = new Appointment
        {
            AppointmentId = Guid.NewGuid(),
            PatientId = patientId,
            PatientName = patient.FullName,
            Mobile = patient.Mobile,
            HospitalId = HospitalId,
            Service = "Chest X-Ray",
            Modality = "XRAY",
            DateTime = DateTime.UtcNow,
            Doctor = "Dr. Original",
            ReferredBy = "Self",
            ArrivedAt = DateTime.UtcNow,
            Status = "CONFIRMED"
        };
        var removedService = new AppointmentService
        {
            AppointmentId = appointment.AppointmentId,
            HospitalId = HospitalId,
            ServiceName = "Chest X-Ray",
            Modality = "XRAY",
            Amount = 500m
        };
        var retainedService = new AppointmentService
        {
            AppointmentId = appointment.AppointmentId,
            HospitalId = HospitalId,
            ServiceName = "Abdomen Ultrasound",
            Modality = "USG",
            Amount = 800m
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.AppointmentId,
            PatientId = patientId,
            PatientName = patient.FullName,
            HospitalId = HospitalId,
            InvoiceId = "INV-EDIT-001",
            GrossAmount = 1300m,
            TotalAmount = 1300m,
            Status = "PENDING",
            CreatedAt = DateTime.UtcNow,
            ServiceDate = appointment.DateTime
        };
        invoice.Items.Add(new InvoiceItem
        {
            Description = removedService.ServiceName,
            Amount = removedService.Amount,
            AppointmentServiceId = removedService.Id
        });
        invoice.Items.Add(new InvoiceItem
        {
            Description = retainedService.ServiceName,
            Amount = retainedService.Amount,
            AppointmentServiceId = retainedService.Id
        });

        Context.Patients.Add(patient);
        Context.Appointments.Add(appointment);
        Context.AppointmentServices.AddRange(removedService, retainedService);
        Context.Invoices.Add(invoice);
        await Context.SaveChangesAsync();

        var result = await _handler.Handle(new UpdateAppointmentCommand(
            appointment.AppointmentId,
            "Renal Ultrasound",
            "USG",
            appointment.DateTime,
            "Dr. Updated",
            "Updated notes",
            "Dr. Updated Referrer",
            ReferredContact: "9222222222",
            PatientName: "Updated Patient",
            Mobile: "9111111111",
            PatientAge: "36",
            PatientGender: "Male",
            Village: "Village A",
            Block: "Block B",
            District: "District C",
            Address: "Updated address",
            SourceOfInfo: "Referral",
            ReferrerIsDoctor: true,
            ReferrerAddress: "Referrer address",
            Services: new[]
            {
                new AppointmentServiceLine("Renal Ultrasound", "USG", 900m, 0m, retainedService.Id),
                new AppointmentServiceLine("CT Head", "CT", 1500m, 0m)
            }),
            CancellationToken.None);

        Assert.True(result.Success);

        var updatedPatient = await Context.Patients.SingleAsync(p => p.PatientId == patientId);
        Assert.Equal("UPDATED PATIENT", updatedPatient.FullName);
        Assert.Equal("9111111111", updatedPatient.Mobile);
        Assert.Equal("36", updatedPatient.Age);
        Assert.Equal("Male", updatedPatient.Gender);
        Assert.Equal("VILLAGE A", updatedPatient.Village);
        Assert.Equal("BLOCK B", updatedPatient.Block);
        Assert.Equal("DISTRICT C", updatedPatient.District);
        Assert.Equal("Updated address", updatedPatient.Address);
        Assert.Equal("Referral", updatedPatient.SourceOfInfo);

        var updatedAppointment = await Context.Appointments.SingleAsync(a => a.AppointmentId == appointment.AppointmentId);
        Assert.Equal("Dr. Updated", updatedAppointment.Doctor);
        Assert.Equal("Updated notes", updatedAppointment.Notes);
        Assert.Equal("DR. UPDATED REFERRER", updatedAppointment.ReferredBy);

        var updatedReferrer = await Context.Referrers.SingleAsync(r => r.Name == "Dr. Updated Referrer");
        Assert.Equal("9222222222", updatedReferrer.Contact);
        Assert.Equal("Referrer address", updatedReferrer.Address);

        var revenueInvoice = (await new GetInvoicesQueryHandler(Context, new InvoiceEnrichmentService(Context))
            .Handle(new GetInvoicesQuery { AppointmentId = appointment.AppointmentId }, CancellationToken.None))
            .Items.Single();

        Assert.Equal(2400m, revenueInvoice.GrossAmount);
        Assert.Equal(2400m, revenueInvoice.TotalAmount);
        Assert.Collection(revenueInvoice.Items.OrderBy(item => item.Description),
            item =>
            {
                Assert.Equal("CT Head", item.Description);
                Assert.Equal(1500m, item.Amount);
                Assert.Equal("CT", item.Modality);
            },
            item =>
            {
                Assert.Equal("Renal Ultrasound", item.Description);
                Assert.Equal(900m, item.Amount);
                Assert.Equal("USG", item.Modality);
            });
        Assert.DoesNotContain(revenueInvoice.Items, item => item.Description == "Chest X-Ray");

        // Production data written by an earlier partial-reconciliation build can
        // contain only the newly added line. Revenue must still expose every live
        // appointment service until that legacy row is repaired on the next edit.
        var retainedInvoiceItem = await Context.Set<InvoiceItem>()
            .SingleAsync(item => item.AppointmentServiceId == retainedService.Id);
        Context.Remove(retainedInvoiceItem);
        await Context.SaveChangesAsync();

        var repairedRevenueInvoice = (await new GetInvoicesQueryHandler(Context, new InvoiceEnrichmentService(Context))
            .Handle(new GetInvoicesQuery { AppointmentId = appointment.AppointmentId }, CancellationToken.None))
            .Items.Single();

        Assert.Equal(2, repairedRevenueInvoice.Items.Count);
        Assert.Contains(repairedRevenueInvoice.Items, item =>
            item.AppointmentServiceId == retainedService.Id
            && item.Description == "Renal Ultrasound"
            && item.Amount == 900m);
        Assert.Contains(repairedRevenueInvoice.Items, item =>
            item.AppointmentServiceId != retainedService.Id
            && item.Description == "CT Head"
            && item.Amount == 1500m);
    }

    [Fact]
    public async Task Handle_PaidServiceEdit_DoesNotCreateAnotherCommission()
    {
        var patient = new Patient
        {
            PatientId = Guid.NewGuid(),
            HospitalId = HospitalId,
            FullName = "Commission Patient"
        };
        var appointment = new Appointment
        {
            AppointmentId = Guid.NewGuid(),
            PatientId = patient.PatientId,
            PatientName = patient.FullName,
            HospitalId = HospitalId,
            Service = "Initial Ultrasound",
            Modality = "USG",
            DateTime = DateTime.UtcNow,
            Doctor = "Dr. Reader",
            ReferredBy = "Dr. Referrer",
            ArrivedAt = DateTime.UtcNow,
            Status = "CONFIRMED"
        };
        var service = new AppointmentService
        {
            AppointmentId = appointment.AppointmentId,
            HospitalId = HospitalId,
            ServiceName = "Initial Ultrasound",
            Modality = "USG",
            Amount = 800m,
            ReferralCutValue = 120m
        };
        var referrer = new Referrer
        {
            ReferrerId = Guid.NewGuid(),
            HospitalId = HospitalId,
            Name = "Dr. Referrer"
        };
        var paidCommission = new ReferralCommission
        {
            Id = Guid.NewGuid(),
            HospitalId = HospitalId,
            AppointmentId = appointment.AppointmentId,
            AppointmentServiceId = service.Id,
            ReferrerId = referrer.ReferrerId,
            ReferrerName = referrer.Name,
            Modality = "USG",
            CommissionAmount = 120m,
            Status = "PAID",
            TransactionDate = DateTime.UtcNow,
            PaymentDate = DateTime.UtcNow
        };

        Context.AddRange(patient, appointment, service, referrer, paidCommission);
        await Context.SaveChangesAsync();

        var result = await _handler.Handle(new UpdateAppointmentCommand(
            appointment.AppointmentId,
            "Updated Ultrasound",
            "USG",
            appointment.DateTime,
            appointment.Doctor,
            "Updated after payment",
            referrer.Name,
            Services: new[]
            {
                new AppointmentServiceLine("Updated Ultrasound", "USG", 900m, 120m, service.Id)
            }),
            CancellationToken.None);

        Assert.True(result.Success);
        var commissions = await Context.ReferralCommissions
            .Where(c => c.AppointmentServiceId == service.Id && c.DeletedAt == null)
            .ToListAsync();
        var onlyCommission = Assert.Single(commissions);
        Assert.Equal(paidCommission.Id, onlyCommission.Id);
        Assert.Equal("PAID", onlyCommission.Status);
        Assert.Equal(120m, onlyCommission.CommissionAmount);
    }
}
