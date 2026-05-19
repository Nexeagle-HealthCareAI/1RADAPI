using _1Rad.Application.Features.Finance.Commands.GenerateInvoice;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance;

public class GenerateInvoiceCommandHandlerTests : BaseHandlerTest
{
    private readonly GenerateInvoiceCommandHandler _handler;

    public GenerateInvoiceCommandHandlerTests()
    {
        _handler = new GenerateInvoiceCommandHandler(Context);
    }

    [Fact]
    public async Task Handle_ValidInvoiceWithAppointment_CreatesInvoiceSuccessfully()
    {
        // Arrange
        var patientId = Guid.NewGuid();
        var appointmentId = Guid.NewGuid();
        
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
            HospitalId = HospitalId
        };

        Context.Patients.Add(patient);
        Context.Appointments.Add(appointment);
        await Context.SaveChangesAsync();

        var command = new GenerateInvoiceCommand
        {
            AppointmentId = appointmentId,
            PatientId = patientId,
            Items = new List<InvoiceItemDto>
            {
                new("X-Ray Chest", 500m, 1),
                new("Consultation", 300m, 1)
            }
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotEqual(Guid.Empty, result);
        
        var invoice = await Context.Invoices.FindAsync(result);
        Assert.NotNull(invoice);
        Assert.Equal(patientId, invoice.PatientId);
        Assert.Equal(appointmentId, invoice.AppointmentId);
        Assert.Equal("John Doe", invoice.PatientName);
        Assert.Equal(800m, invoice.TotalAmount);
        Assert.Equal(0m, invoice.PaidAmount);
        Assert.Equal("PENDING", invoice.Status);
        Assert.Equal(2, invoice.Items.Count);
        Assert.StartsWith("INV-", invoice.InvoiceId);
    }

    [Fact]
    public async Task Handle_ValidInvoiceWithoutAppointment_CreatesInvoiceSuccessfully()
    {
        // Arrange
        var patientId = Guid.NewGuid();
        var patient = new Patient
        {
            PatientId = patientId,
            FullName = "Jane Smith",
            HospitalId = HospitalId
        };

        Context.Patients.Add(patient);
        await Context.SaveChangesAsync();

        var command = new GenerateInvoiceCommand
        {
            AppointmentId = null,
            PatientId = patientId,
            Items = new List<InvoiceItemDto>
            {
                new("MRI Scan", 3000m, 1)
            }
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotEqual(Guid.Empty, result);
        
        var invoice = await Context.Invoices.FindAsync(result);
        Assert.NotNull(invoice);
        Assert.Null(invoice.AppointmentId);
        Assert.Equal(3000m, invoice.TotalAmount);
    }

    [Fact]
    public async Task Handle_EmptyItemsList_ThrowsArgumentException()
    {
        // Arrange
        var command = new GenerateInvoiceCommand
        {
            PatientId = Guid.NewGuid(),
            Items = new List<InvoiceItemDto>()
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.Handle(command, CancellationToken.None));
        Assert.Contains("at least one item", exception.Message);
    }

    [Fact]
    public async Task Handle_NullItemsList_ThrowsArgumentException()
    {
        // Arrange
        var command = new GenerateInvoiceCommand
        {
            PatientId = Guid.NewGuid(),
            Items = null!
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_InvalidItemAmount_ThrowsArgumentException()
    {
        // Arrange
        var command = new GenerateInvoiceCommand
        {
            PatientId = Guid.NewGuid(),
            Items = new List<InvoiceItemDto>
            {
                new("X-Ray", 0m, 1) // Invalid amount
            }
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.Handle(command, CancellationToken.None));
        Assert.Contains("invalid amount", exception.Message);
    }

    [Fact]
    public async Task Handle_NegativeItemAmount_ThrowsArgumentException()
    {
        // Arrange
        var command = new GenerateInvoiceCommand
        {
            PatientId = Guid.NewGuid(),
            Items = new List<InvoiceItemDto>
            {
                new("X-Ray", -100m, 1)
            }
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_InvalidItemQuantity_ThrowsArgumentException()
    {
        // Arrange
        var command = new GenerateInvoiceCommand
        {
            PatientId = Guid.NewGuid(),
            Items = new List<InvoiceItemDto>
            {
                new("X-Ray", 500m, 0) // Invalid quantity
            }
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.Handle(command, CancellationToken.None));
        Assert.Contains("invalid quantity", exception.Message);
    }

    [Fact]
    public async Task Handle_PatientNotFound_ThrowsKeyNotFoundException()
    {
        // Arrange
        var command = new GenerateInvoiceCommand
        {
            PatientId = Guid.NewGuid(),
            Items = new List<InvoiceItemDto>
            {
                new("X-Ray", 500m, 1)
            }
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _handler.Handle(command, CancellationToken.None));
        Assert.Contains("Patient", exception.Message);
    }

    [Fact]
    public async Task Handle_PatientFromDifferentHospital_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var patientId = Guid.NewGuid();
        var patient = new Patient
        {
            PatientId = patientId,
            FullName = "John Doe",
            HospitalId = Guid.NewGuid() // Different hospital
        };

        Context.Patients.Add(patient);
        await Context.SaveChangesAsync();

        var command = new GenerateInvoiceCommand
        {
            PatientId = patientId,
            Items = new List<InvoiceItemDto>
            {
                new("X-Ray", 500m, 1)
            }
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _handler.Handle(command, CancellationToken.None));
        Assert.Contains("does not belong to your hospital", exception.Message);
    }

    [Fact]
    public async Task Handle_AppointmentNotFound_ThrowsKeyNotFoundException()
    {
        // Arrange
        var patientId = Guid.NewGuid();
        var patient = new Patient
        {
            PatientId = patientId,
            FullName = "John Doe",
            HospitalId = HospitalId
        };

        Context.Patients.Add(patient);
        await Context.SaveChangesAsync();

        var command = new GenerateInvoiceCommand
        {
            AppointmentId = Guid.NewGuid(),
            PatientId = patientId,
            Items = new List<InvoiceItemDto>
            {
                new("X-Ray", 500m, 1)
            }
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _handler.Handle(command, CancellationToken.None));
        Assert.Contains("Appointment", exception.Message);
    }

    [Fact]
    public async Task Handle_AppointmentPatientMismatch_ThrowsInvalidOperationException()
    {
        // Arrange
        var patientId = Guid.NewGuid();
        var appointmentId = Guid.NewGuid();
        
        var patient = new Patient
        {
            PatientId = patientId,
            FullName = "John Doe",
            HospitalId = HospitalId
        };

        var appointment = new Appointment
        {
            AppointmentId = appointmentId,
            PatientId = Guid.NewGuid(), // Different patient
            PatientName = "John Doe Mismatch",
            HospitalId = HospitalId
        };

        Context.Patients.Add(patient);
        Context.Appointments.Add(appointment);
        await Context.SaveChangesAsync();

        var command = new GenerateInvoiceCommand
        {
            AppointmentId = appointmentId,
            PatientId = patientId,
            Items = new List<InvoiceItemDto>
            {
                new("X-Ray", 500m, 1)
            }
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _handler.Handle(command, CancellationToken.None));
        Assert.Contains("does not belong to the specified patient", exception.Message);
    }

    [Fact]
    public async Task Handle_MultipleItems_CalculatesTotalCorrectly()
    {
        // Arrange
        var patientId = Guid.NewGuid();
        var patient = new Patient
        {
            PatientId = patientId,
            FullName = "John Doe",
            HospitalId = HospitalId
        };

        Context.Patients.Add(patient);
        await Context.SaveChangesAsync();

        var command = new GenerateInvoiceCommand
        {
            PatientId = patientId,
            Items = new List<InvoiceItemDto>
            {
                new("X-Ray", 500m, 2),      // 1000
                new("MRI", 3000m, 1),       // 3000
                new("Consultation", 300m, 3) // 900
            }
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        var invoice = await Context.Invoices.FindAsync(result);
        Assert.NotNull(invoice);
        Assert.Equal(4900m, invoice.TotalAmount);
        Assert.Equal(3, invoice.Items.Count);
    }

    [Fact]
    public async Task Handle_EmptyHospitalContext_UsesPatientHospitalId()
    {
        // Arrange
        MockUserContext.Setup(x => x.HospitalId).Returns(Guid.Empty);

        var patientId = Guid.NewGuid();
        var patient = new Patient
        {
            PatientId = patientId,
            FullName = "John Doe",
            HospitalId = HospitalId
        };

        Context.Patients.Add(patient);
        await Context.SaveChangesAsync();

        var command = new GenerateInvoiceCommand
        {
            PatientId = patientId,
            Items = new List<InvoiceItemDto>
            {
                new("X-Ray", 500m, 1)
            }
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        var invoice = await Context.Invoices.FindAsync(result);
        Assert.NotNull(invoice);
        Assert.Equal(HospitalId, invoice.HospitalId);
    }
}
