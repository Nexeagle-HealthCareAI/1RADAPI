using _1Rad.Application.Features.Finance.Commands.GenerateInvoice;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.UnitTests.Helpers;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance;

public class GenerateInvoiceCommandHandlerTests
{
    private readonly Mock<IApplicationDbContext> _mockContext;
    private readonly Mock<IUserContext> _mockUserContext;
    private readonly Mock<DbSet<Patient>> _mockPatientSet;
    private readonly Mock<DbSet<Appointment>> _mockAppointmentSet;
    private readonly Mock<DbSet<Invoice>> _mockInvoiceSet;
    private readonly GenerateInvoiceCommandHandler _handler;
    private readonly Guid _hospitalId = Guid.NewGuid();
    private readonly Guid _patientId = Guid.NewGuid();
    private readonly Guid _appointmentId = Guid.NewGuid();

    public GenerateInvoiceCommandHandlerTests()
    {
        _mockContext = new Mock<IApplicationDbContext>();
        _mockUserContext = new Mock<IUserContext>();
        _mockPatientSet = new Mock<DbSet<Patient>>();
        _mockAppointmentSet = new Mock<DbSet<Appointment>>();
        _mockInvoiceSet = new Mock<DbSet<Invoice>>();

        _mockUserContext.Setup(x => x.HospitalId).Returns(_hospitalId);
        _mockContext.Setup(x => x.UserContext).Returns(_mockUserContext.Object);
        _mockContext.Setup(x => x.Patients).Returns(_mockPatientSet.Object);
        _mockContext.Setup(x => x.Appointments).Returns(_mockAppointmentSet.Object);
        _mockContext.Setup(x => x.Invoices).Returns(_mockInvoiceSet.Object);

        _handler = new GenerateInvoiceCommandHandler(_mockContext.Object);
    }

    [Fact]
    public async Task Handle_ValidInvoiceWithAppointment_CreatesInvoiceSuccessfully()
    {
        // Arrange
        var patient = new Patient
        {
            PatientId = _patientId,
            FullName = "John Doe",
            HospitalId = _hospitalId
        };

        var appointment = new Appointment
        {
            AppointmentId = _appointmentId,
            PatientId = _patientId,
            HospitalId = _hospitalId
        };

        var patients = new List<Patient> { patient }.AsQueryable();
        var appointments = new List<Appointment> { appointment }.AsQueryable();

        SetupMockDbSet(_mockPatientSet, patients);
        SetupMockDbSet(_mockAppointmentSet, appointments);

        Invoice capturedInvoice = null!;
        _mockInvoiceSet.Setup(x => x.Add(It.IsAny<Invoice>()))
            .Callback<Invoice>(i => capturedInvoice = i);

        var command = new GenerateInvoiceCommand
        {
            AppointmentId = _appointmentId,
            PatientId = _patientId,
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
        Assert.NotNull(capturedInvoice);
        Assert.Equal(_patientId, capturedInvoice.PatientId);
        Assert.Equal(_appointmentId, capturedInvoice.AppointmentId);
        Assert.Equal("John Doe", capturedInvoice.PatientName);
        Assert.Equal(800m, capturedInvoice.TotalAmount);
        Assert.Equal(0m, capturedInvoice.PaidAmount);
        Assert.Equal("PENDING", capturedInvoice.Status);
        Assert.Equal(2, capturedInvoice.Items.Count);
        Assert.StartsWith("INV-", capturedInvoice.InvoiceId);
        _mockContext.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ValidInvoiceWithoutAppointment_CreatesInvoiceSuccessfully()
    {
        // Arrange
        var patient = new Patient
        {
            PatientId = _patientId,
            FullName = "Jane Smith",
            HospitalId = _hospitalId
        };

        var patients = new List<Patient> { patient }.AsQueryable();
        SetupMockDbSet(_mockPatientSet, patients);

        var command = new GenerateInvoiceCommand
        {
            AppointmentId = null,
            PatientId = _patientId,
            Items = new List<InvoiceItemDto>
            {
                new("MRI Scan", 3000m, 1)
            }
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotEqual(Guid.Empty, result);
        _mockInvoiceSet.Verify(x => x.Add(It.Is<Invoice>(i => 
            i.AppointmentId == null && 
            i.TotalAmount == 3000m)), Times.Once);
    }

    [Fact]
    public async Task Handle_EmptyItemsList_ThrowsArgumentException()
    {
        // Arrange
        var command = new GenerateInvoiceCommand
        {
            PatientId = _patientId,
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
            PatientId = _patientId,
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
            PatientId = _patientId,
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
            PatientId = _patientId,
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
            PatientId = _patientId,
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
        var patients = new List<Patient>().AsQueryable();
        SetupMockDbSet(_mockPatientSet, patients);

        var command = new GenerateInvoiceCommand
        {
            PatientId = _patientId,
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
        var patient = new Patient
        {
            PatientId = _patientId,
            FullName = "John Doe",
            HospitalId = Guid.NewGuid() // Different hospital
        };

        var patients = new List<Patient> { patient }.AsQueryable();
        SetupMockDbSet(_mockPatientSet, patients);

        var command = new GenerateInvoiceCommand
        {
            PatientId = _patientId,
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
        var patient = new Patient
        {
            PatientId = _patientId,
            FullName = "John Doe",
            HospitalId = _hospitalId
        };

        var patients = new List<Patient> { patient }.AsQueryable();
        var appointments = new List<Appointment>().AsQueryable();

        SetupMockDbSet(_mockPatientSet, patients);
        SetupMockDbSet(_mockAppointmentSet, appointments);

        var command = new GenerateInvoiceCommand
        {
            AppointmentId = _appointmentId,
            PatientId = _patientId,
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
        var patient = new Patient
        {
            PatientId = _patientId,
            FullName = "John Doe",
            HospitalId = _hospitalId
        };

        var appointment = new Appointment
        {
            AppointmentId = _appointmentId,
            PatientId = Guid.NewGuid(), // Different patient
            HospitalId = _hospitalId
        };

        var patients = new List<Patient> { patient }.AsQueryable();
        var appointments = new List<Appointment> { appointment }.AsQueryable();

        SetupMockDbSet(_mockPatientSet, patients);
        SetupMockDbSet(_mockAppointmentSet, appointments);

        var command = new GenerateInvoiceCommand
        {
            AppointmentId = _appointmentId,
            PatientId = _patientId,
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
        var patient = new Patient
        {
            PatientId = _patientId,
            FullName = "John Doe",
            HospitalId = _hospitalId
        };

        var patients = new List<Patient> { patient }.AsQueryable();
        SetupMockDbSet(_mockPatientSet, patients);

        Invoice capturedInvoice = null!;
        _mockInvoiceSet.Setup(x => x.Add(It.IsAny<Invoice>()))
            .Callback<Invoice>(i => capturedInvoice = i);

        var command = new GenerateInvoiceCommand
        {
            PatientId = _patientId,
            Items = new List<InvoiceItemDto>
            {
                new("X-Ray", 500m, 2),      // 1000
                new("MRI", 3000m, 1),       // 3000
                new("Consultation", 300m, 3) // 900
            }
        };

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.Equal(4900m, capturedInvoice.TotalAmount);
        Assert.Equal(3, capturedInvoice.Items.Count);
    }

    [Fact]
    public async Task Handle_EmptyHospitalContext_UsesPatientHospitalId()
    {
        // Arrange
        _mockUserContext.Setup(x => x.HospitalId).Returns(Guid.Empty);

        var patient = new Patient
        {
            PatientId = _patientId,
            FullName = "John Doe",
            HospitalId = _hospitalId
        };

        var patients = new List<Patient> { patient }.AsQueryable();
        SetupMockDbSet(_mockPatientSet, patients);

        Invoice capturedInvoice = null!;
        _mockInvoiceSet.Setup(x => x.Add(It.IsAny<Invoice>()))
            .Callback<Invoice>(i => capturedInvoice = i);

        var command = new GenerateInvoiceCommand
        {
            PatientId = _patientId,
            Items = new List<InvoiceItemDto>
            {
                new("X-Ray", 500m, 1)
            }
        };

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.Equal(_hospitalId, capturedInvoice.HospitalId);
    }

    private void SetupMockDbSet<T>(Mock<DbSet<T>> mockSet, IQueryable<T> data) where T : class
    {
        mockSet.As<IAsyncEnumerable<T>>()
            .Setup(m => m.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(new TestAsyncEnumerator<T>(data.GetEnumerator()));

        mockSet.As<IQueryable<T>>()
            .Setup(m => m.Provider)
            .Returns(new TestAsyncQueryProvider<T>(data.Provider));

        mockSet.As<IQueryable<T>>().Setup(m => m.Expression).Returns(data.Expression);
        mockSet.As<IQueryable<T>>().Setup(m => m.ElementType).Returns(data.ElementType);
        mockSet.As<IQueryable<T>>().Setup(m => m.GetEnumerator()).Returns(data.GetEnumerator());
        
        // Support for IgnoreQueryFilters
        mockSet.Setup(m => m.IgnoreQueryFilters()).Returns(mockSet.Object);
    }
}
