using _1Rad.Application.Features.Finance.Commands.CollectPayment;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.UnitTests.Helpers;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance;

public class CollectPaymentCommandHandlerTests
{
    private readonly Mock<IApplicationDbContext> _mockContext;
    private readonly Mock<IUserContext> _mockUserContext;
    private readonly Mock<DbSet<Invoice>> _mockInvoiceSet;
    private readonly Mock<DbSet<Payment>> _mockPaymentSet;
    private readonly CollectPaymentCommandHandler _handler;
    private readonly Guid _hospitalId = Guid.NewGuid();
    private readonly Guid _invoiceId = Guid.NewGuid();

    public CollectPaymentCommandHandlerTests()
    {
        _mockContext = new Mock<IApplicationDbContext>();
        _mockUserContext = new Mock<IUserContext>();
        _mockInvoiceSet = new Mock<DbSet<Invoice>>();
        _mockPaymentSet = new Mock<DbSet<Payment>>();

        _mockUserContext.Setup(x => x.HospitalId).Returns(_hospitalId);
        _mockContext.Setup(x => x.UserContext).Returns(_mockUserContext.Object);
        _mockContext.Setup(x => x.Invoices).Returns(_mockInvoiceSet.Object);
        _mockContext.Setup(x => x.Payments).Returns(_mockPaymentSet.Object);

        _handler = new CollectPaymentCommandHandler(_mockContext.Object);
    }

    [Fact]
    public async Task Handle_ValidFullPayment_UpdatesInvoiceStatusToPaid()
    {
        // Arrange
        var invoice = new Invoice
        {
            Id = _invoiceId,
            InvoiceId = "INV-001",
            HospitalId = _hospitalId,
            TotalAmount = 1000m,
            PaidAmount = 0m,
            Status = "PENDING",
            Payments = new List<Payment>()
        };

        var invoices = new List<Invoice> { invoice }.AsQueryable();
        SetupMockDbSet(_mockInvoiceSet, invoices);

        var command = new CollectPaymentCommand
        {
            InvoiceId = _invoiceId,
            Amount = 1000m,
            PaymentMethod = "CASH"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result);
        Assert.Equal("PAID", invoice.Status);
        Assert.Equal(1000m, invoice.PaidAmount);
        Assert.NotNull(invoice.PaidAt);
        _mockPaymentSet.Verify(x => x.Add(It.IsAny<Payment>()), Times.Once);
        _mockContext.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ValidPartialPayment_UpdatesInvoiceStatusToPartial()
    {
        // Arrange
        var invoice = new Invoice
        {
            Id = _invoiceId,
            InvoiceId = "INV-001",
            HospitalId = _hospitalId,
            TotalAmount = 1000m,
            PaidAmount = 0m,
            Status = "PENDING",
            Payments = new List<Payment>()
        };

        var invoices = new List<Invoice> { invoice }.AsQueryable();
        SetupMockDbSet(_mockInvoiceSet, invoices);

        var command = new CollectPaymentCommand
        {
            InvoiceId = _invoiceId,
            Amount = 500m,
            PaymentMethod = "UPI"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result);
        Assert.Equal("PARTIAL", invoice.Status);
        Assert.Equal(500m, invoice.PaidAmount);
        Assert.Null(invoice.PaidAt);
    }

    [Fact]
    public async Task Handle_EmptyHospitalContext_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        _mockUserContext.Setup(x => x.HospitalId).Returns(Guid.Empty);

        var command = new CollectPaymentCommand
        {
            InvoiceId = _invoiceId,
            Amount = 100m,
            PaymentMethod = "CASH"
        };

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_ZeroAmount_ThrowsArgumentException()
    {
        // Arrange
        var command = new CollectPaymentCommand
        {
            InvoiceId = _invoiceId,
            Amount = 0m,
            PaymentMethod = "CASH"
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_NegativeAmount_ThrowsArgumentException()
    {
        // Arrange
        var command = new CollectPaymentCommand
        {
            InvoiceId = _invoiceId,
            Amount = -100m,
            PaymentMethod = "CASH"
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_InvoiceNotFound_ThrowsKeyNotFoundException()
    {
        // Arrange
        var invoices = new List<Invoice>().AsQueryable();
        SetupMockDbSet(_mockInvoiceSet, invoices);

        var command = new CollectPaymentCommand
        {
            InvoiceId = _invoiceId,
            Amount = 100m,
            PaymentMethod = "CASH"
        };

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_InvoiceFromDifferentHospital_ThrowsKeyNotFoundException()
    {
        // Arrange
        var invoice = new Invoice
        {
            Id = _invoiceId,
            InvoiceId = "INV-001",
            HospitalId = Guid.NewGuid(), // Different hospital
            TotalAmount = 1000m,
            PaidAmount = 0m,
            Status = "PENDING",
            Payments = new List<Payment>()
        };

        var invoices = new List<Invoice> { invoice }.AsQueryable();
        SetupMockDbSet(_mockInvoiceSet, invoices);

        var command = new CollectPaymentCommand
        {
            InvoiceId = _invoiceId,
            Amount = 100m,
            PaymentMethod = "CASH"
        };

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_AlreadyPaidInvoice_ThrowsInvalidOperationException()
    {
        // Arrange
        var invoice = new Invoice
        {
            Id = _invoiceId,
            InvoiceId = "INV-001",
            HospitalId = _hospitalId,
            TotalAmount = 1000m,
            PaidAmount = 1000m,
            Status = "PAID",
            Payments = new List<Payment>()
        };

        var invoices = new List<Invoice> { invoice }.AsQueryable();
        SetupMockDbSet(_mockInvoiceSet, invoices);

        var command = new CollectPaymentCommand
        {
            InvoiceId = _invoiceId,
            Amount = 100m,
            PaymentMethod = "CASH"
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_CancelledInvoice_ThrowsInvalidOperationException()
    {
        // Arrange
        var invoice = new Invoice
        {
            Id = _invoiceId,
            InvoiceId = "INV-001",
            HospitalId = _hospitalId,
            TotalAmount = 1000m,
            PaidAmount = 0m,
            Status = "CANCELLED",
            Payments = new List<Payment>()
        };

        var invoices = new List<Invoice> { invoice }.AsQueryable();
        SetupMockDbSet(_mockInvoiceSet, invoices);

        var command = new CollectPaymentCommand
        {
            InvoiceId = _invoiceId,
            Amount = 100m,
            PaymentMethod = "CASH"
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_PaymentExceedsRemainingBalance_ThrowsInvalidOperationException()
    {
        // Arrange
        var invoice = new Invoice
        {
            Id = _invoiceId,
            InvoiceId = "INV-001",
            HospitalId = _hospitalId,
            TotalAmount = 1000m,
            PaidAmount = 800m,
            Status = "PARTIAL",
            Payments = new List<Payment>()
        };

        var invoices = new List<Invoice> { invoice }.AsQueryable();
        SetupMockDbSet(_mockInvoiceSet, invoices);

        var command = new CollectPaymentCommand
        {
            InvoiceId = _invoiceId,
            Amount = 300m, // Exceeds remaining 200
            PaymentMethod = "CASH"
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_ValidPayment_CreatesPaymentWithCorrectProperties()
    {
        // Arrange
        var invoice = new Invoice
        {
            Id = _invoiceId,
            InvoiceId = "INV-001",
            HospitalId = _hospitalId,
            TotalAmount = 1000m,
            PaidAmount = 0m,
            Status = "PENDING",
            Payments = new List<Payment>()
        };

        var invoices = new List<Invoice> { invoice }.AsQueryable();
        SetupMockDbSet(_mockInvoiceSet, invoices);

        Payment capturedPayment = null!;
        _mockPaymentSet.Setup(x => x.Add(It.IsAny<Payment>()))
            .Callback<Payment>(p => capturedPayment = p);

        var command = new CollectPaymentCommand
        {
            InvoiceId = _invoiceId,
            Amount = 500m,
            PaymentMethod = "CARD"
        };

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedPayment);
        Assert.Equal(_invoiceId, capturedPayment.InvoiceId);
        Assert.Equal(500m, capturedPayment.Amount);
        Assert.Equal("CARD", capturedPayment.PaymentMethod);
        Assert.Equal(_hospitalId, capturedPayment.HospitalId);
        Assert.NotEqual(Guid.Empty, capturedPayment.Id);
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
