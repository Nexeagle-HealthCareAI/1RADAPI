using _1Rad.Application.Features.Finance.Commands.DeleteInvoice;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance;

public class DeleteInvoiceCommandHandlerTests : BaseHandlerTest
{
    private readonly DeleteInvoiceCommandHandler _handler;

    public DeleteInvoiceCommandHandlerTests()
    {
        _handler = new DeleteInvoiceCommandHandler(Context);
    }

    [Fact]
    public async Task Handle_WithUnpaidInvoiceAndScannedAppointmentStatus_ReturnsFailure()
    {
        // Arrange
        var patientId = Guid.NewGuid();
        var appointmentId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();

        var patient = new Patient
        {
            PatientId = patientId,
            FullName = "John Doe",
            HospitalId = HospitalId
        };

        var appointment = new Appointment
        {
            AppointmentId = appointmentId,
            PatientId = patientId,
            PatientName = "John Doe",
            HospitalId = HospitalId,
            Status = "Scanned" // Scanned status
        };

        var invoice = new Invoice
        {
            Id = invoiceId,
            InvoiceId = "INV-001",
            PatientId = patientId,
            AppointmentId = appointmentId,
            TotalAmount = 500m,
            PaidAmount = 0m,
            Status = "PENDING",
            HospitalId = HospitalId
        };

        Context.Patients.Add(patient);
        Context.Appointments.Add(appointment);
        Context.Invoices.Add(invoice);
        await Context.SaveChangesAsync();

        var command = new DeleteInvoiceCommand(invoiceId);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("cannot be deleted because the associated study has already been scanned or processed", result.Error);
    }

    [Fact]
    public async Task Handle_WithValidUnpaidInvoiceAndScheduledAppointment_DeletesSuccessfully()
    {
        // Arrange
        var patientId = Guid.NewGuid();
        var appointmentId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();

        var patient = new Patient
        {
            PatientId = patientId,
            FullName = "Jane Doe",
            HospitalId = HospitalId
        };

        var appointment = new Appointment
        {
            AppointmentId = appointmentId,
            PatientId = patientId,
            PatientName = "Jane Doe",
            HospitalId = HospitalId,
            Status = "Scheduled" // Scheduled status is allowed to delete
        };

        var invoice = new Invoice
        {
            Id = invoiceId,
            InvoiceId = "INV-002",
            PatientId = patientId,
            AppointmentId = appointmentId,
            TotalAmount = 500m,
            PaidAmount = 0m,
            Status = "PENDING",
            HospitalId = HospitalId
        };

        Context.Patients.Add(patient);
        Context.Appointments.Add(appointment);
        Context.Invoices.Add(invoice);
        await Context.SaveChangesAsync();

        var command = new DeleteInvoiceCommand(invoiceId);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Null(result.Error);
        var deletedInvoice = await Context.Invoices.FindAsync(invoiceId);
        Assert.Null(deletedInvoice);
    }
}
