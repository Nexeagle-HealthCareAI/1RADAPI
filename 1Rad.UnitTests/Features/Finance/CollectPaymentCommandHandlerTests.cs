using _1Rad.Application.Features.Finance.Commands.CollectPayment;
using _1Rad.Domain.Entities;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance;

public class CollectPaymentCommandHandlerTests : BaseHandlerTest
{
    private readonly CollectPaymentCommandHandler _handler;

    public CollectPaymentCommandHandlerTests()
    {
        _handler = new CollectPaymentCommandHandler(Context);
    }

    [Fact]
    public async Task Handle_ValidFullPayment_UpdatesInvoiceStatusToPaid()
    {
        // Arrange
        var invoiceId = Guid.NewGuid();
        var invoice = new Invoice
        {
            Id = invoiceId,
            InvoiceId = "INV-001",
            HospitalId = HospitalId,
            TotalAmount = 1000m,
            PaidAmount = 0m,
            Status = "PENDING",
            Payments = new List<Payment>()
        };

        Context.Invoices.Add(invoice);
        await Context.SaveChangesAsync();

        var command = new CollectPaymentCommand
        {
            InvoiceId = invoiceId,
            Amount = 1000m,
            PaymentMethod = "CASH"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result);
        
        var updatedInvoice = await Context.Invoices.FindAsync(invoiceId);
        Assert.NotNull(updatedInvoice);
        Assert.Equal("PAID", updatedInvoice.Status);
        Assert.Equal(1000m, updatedInvoice.PaidAmount);
        Assert.NotNull(updatedInvoice.PaidAt);
        
        var payment = Context.Payments.FirstOrDefault(p => p.InvoiceId == invoiceId);
        Assert.NotNull(payment);
        Assert.Equal(1000m, payment.Amount);
        Assert.Equal("CASH", payment.PaymentMethod);
    }

    [Fact]
    public async Task Handle_ValidPartialPayment_UpdatesInvoiceStatusToPartial()
    {
        // Arrange
        var invoiceId = Guid.NewGuid();
        var invoice = new Invoice
        {
            Id = invoiceId,
            InvoiceId = "INV-001",
            HospitalId = HospitalId,
            TotalAmount = 1000m,
            PaidAmount = 0m,
            Status = "PENDING",
            Payments = new List<Payment>()
        };

        Context.Invoices.Add(invoice);
        await Context.SaveChangesAsync();

        var command = new CollectPaymentCommand
        {
            InvoiceId = invoiceId,
            Amount = 500m,
            PaymentMethod = "UPI"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result);
        
        var updatedInvoice = await Context.Invoices.FindAsync(invoiceId);
        Assert.NotNull(updatedInvoice);
        Assert.Equal("PARTIAL", updatedInvoice.Status);
        Assert.Equal(500m, updatedInvoice.PaidAmount);
        Assert.Null(updatedInvoice.PaidAt);
    }

    [Fact]
    public async Task Handle_EmptyHospitalContext_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        MockUserContext.Setup(x => x.HospitalId).Returns(Guid.Empty);

        var command = new CollectPaymentCommand
        {
            InvoiceId = Guid.NewGuid(),
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
            InvoiceId = Guid.NewGuid(),
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
            InvoiceId = Guid.NewGuid(),
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
        var command = new CollectPaymentCommand
        {
            InvoiceId = Guid.NewGuid(),
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
        var invoiceId = Guid.NewGuid();
        var invoice = new Invoice
        {
            Id = invoiceId,
            InvoiceId = "INV-001",
            HospitalId = Guid.NewGuid(), // Different hospital
            TotalAmount = 1000m,
            PaidAmount = 0m,
            Status = "PENDING",
            Payments = new List<Payment>()
        };

        Context.Invoices.Add(invoice);
        await Context.SaveChangesAsync();

        var command = new CollectPaymentCommand
        {
            InvoiceId = invoiceId,
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
        var invoiceId = Guid.NewGuid();
        var invoice = new Invoice
        {
            Id = invoiceId,
            InvoiceId = "INV-001",
            HospitalId = HospitalId,
            TotalAmount = 1000m,
            PaidAmount = 1000m,
            Status = "PAID",
            Payments = new List<Payment>()
        };

        Context.Invoices.Add(invoice);
        await Context.SaveChangesAsync();

        var command = new CollectPaymentCommand
        {
            InvoiceId = invoiceId,
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
        var invoiceId = Guid.NewGuid();
        var invoice = new Invoice
        {
            Id = invoiceId,
            InvoiceId = "INV-001",
            HospitalId = HospitalId,
            TotalAmount = 1000m,
            PaidAmount = 0m,
            Status = "CANCELLED",
            Payments = new List<Payment>()
        };

        Context.Invoices.Add(invoice);
        await Context.SaveChangesAsync();

        var command = new CollectPaymentCommand
        {
            InvoiceId = invoiceId,
            Amount = 100m,
            PaymentMethod = "CASH"
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_PaymentExceedsRemainingBalance_CreatesPatientAdvance()
    {
        // Arrange
        var invoiceId = Guid.NewGuid();
        var invoice = new Invoice
        {
            Id = invoiceId,
            InvoiceId = "INV-001",
            HospitalId = HospitalId,
            TotalAmount = 1000m,
            PaidAmount = 800m,
            Status = "PARTIAL",
            Payments = new List<Payment>()
        };

        Context.Invoices.Add(invoice);
        await Context.SaveChangesAsync();

        var command = new CollectPaymentCommand
        {
            InvoiceId = invoiceId,
            Amount = 300m, // Exceeds remaining 200
            PaymentMethod = "CASH"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.True(result);
        var updatedInvoice = await Context.Invoices.FindAsync(invoiceId);
        Assert.NotNull(updatedInvoice);
        Assert.Equal(1000m, updatedInvoice.PaidAmount);
        Assert.Equal("PAID", updatedInvoice.Status);

        var advance = Context.CreditTransactions.Single();
        Assert.Equal("ADVANCE", advance.Type);
        Assert.Equal(100m, advance.Amount);
        Assert.Equal(invoiceId, advance.InvoiceId);
    }

    [Fact]
    public async Task Handle_ValidPayment_CreatesPaymentWithCorrectProperties()
    {
        // Arrange
        var invoiceId = Guid.NewGuid();
        var invoice = new Invoice
        {
            Id = invoiceId,
            InvoiceId = "INV-001",
            HospitalId = HospitalId,
            TotalAmount = 1000m,
            PaidAmount = 0m,
            Status = "PENDING",
            Payments = new List<Payment>()
        };

        Context.Invoices.Add(invoice);
        await Context.SaveChangesAsync();

        var command = new CollectPaymentCommand
        {
            InvoiceId = invoiceId,
            Amount = 500m,
            PaymentMethod = "CARD"
        };

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        var payment = Context.Payments.FirstOrDefault(p => p.InvoiceId == invoiceId);
        Assert.NotNull(payment);
        Assert.Equal(invoiceId, payment.InvoiceId);
        Assert.Equal(500m, payment.Amount);
        Assert.Equal("CARD", payment.PaymentMethod);
        Assert.Equal(HospitalId, payment.HospitalId);
        Assert.NotEqual(Guid.Empty, payment.Id);
    }

    [Fact]
    public async Task Handle_SettlementWithExtraCharge_AppliesTheChargeExactlyOnce()
    {
        var invoiceId = Guid.NewGuid();
        Context.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            InvoiceId = "INV-EXTRA-001",
            HospitalId = HospitalId,
            GrossAmount = 1000m,
            TotalAmount = 1000m,
            Status = "PENDING"
        });
        await Context.SaveChangesAsync();

        await _handler.Handle(new CollectPaymentCommand
        {
            InvoiceId = invoiceId,
            Amount = 1500m,
            PaymentMethod = "CASH",
            ExtraCharges = new List<ExtraChargeDetail>
            {
                new() { Reason = "Night service", Amount = 500m }
            }
        }, CancellationToken.None);

        var invoice = await Context.Invoices.FindAsync(invoiceId);
        Assert.NotNull(invoice);
        Assert.Equal(500m, invoice.AdditionalCharges);
        Assert.Equal(1500m, invoice.TotalAmount);
        Assert.Equal(1500m, invoice.PaidAmount);

        var charges = Context.InvoiceExtraCharges.Where(charge => charge.InvoiceId == invoiceId).ToList();
        var charge = Assert.Single(charges);
        Assert.Equal("Night service", charge.Reason);
        Assert.Equal(500m, charge.Amount);
    }

    [Fact]
    public async Task Handle_ResaveWithEmptyExtraChargesList_ClearsThePreviousCharge()
    {
        // Regression for Critical Issue 06 — a prior save recorded an extra
        // charge; the drawer is reopened, the user removes it, and resaves
        // with an explicit empty ExtraCharges list. That must actually clear
        // the row rather than leaving it orphaned while blanking the reason.
        var invoiceId = Guid.NewGuid();
        Context.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            InvoiceId = "INV-EXTRA-002",
            HospitalId = HospitalId,
            GrossAmount = 1000m,
            TotalAmount = 1000m,
            Status = "PENDING"
        });
        await Context.SaveChangesAsync();

        await _handler.Handle(new CollectPaymentCommand
        {
            InvoiceId = invoiceId,
            Amount = 200m,
            PaymentMethod = "CASH",
            ExtraCharges = new List<ExtraChargeDetail>
            {
                new() { Reason = "Night service", Amount = 500m }
            }
        }, CancellationToken.None);

        await _handler.Handle(new CollectPaymentCommand
        {
            InvoiceId = invoiceId,
            Amount = 0m,
            PaymentMethod = "CASH",
            ExtraCharges = new List<ExtraChargeDetail>()
        }, CancellationToken.None);

        var invoice = await Context.Invoices.FindAsync(invoiceId);
        Assert.NotNull(invoice);
        Assert.Equal(0m, invoice.AdditionalCharges);
        Assert.Equal("[]", invoice.AdditionalChargesReason);
        Assert.Empty(Context.InvoiceExtraCharges.Where(charge => charge.InvoiceId == invoiceId));
    }
}
