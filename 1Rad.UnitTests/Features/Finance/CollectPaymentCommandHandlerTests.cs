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
    public async Task Handle_ZeroAmount_OnMissingBill_ThrowsNotFound()
    {
        // Arrange
        var command = new CollectPaymentCommand
        {
            InvoiceId = Guid.NewGuid(),
            Amount = 0m,
            PaymentMethod = "CASH"
        };

        // Act & Assert — ₹0 is a valid "discount only" settlement; the bill doesn't exist.
        await Assert.ThrowsAsync<_1Rad.Domain.Exceptions.NotFoundException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_NegativeAmount_ThrowsValidationException()
    {
        // Arrange
        var command = new CollectPaymentCommand
        {
            InvoiceId = Guid.NewGuid(),
            Amount = -100m,
            PaymentMethod = "CASH"
        };

        // Act & Assert
        await Assert.ThrowsAsync<_1Rad.Domain.Exceptions.ValidationException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_InvoiceNotFound_ThrowsNotFoundException()
    {
        // Arrange
        var command = new CollectPaymentCommand
        {
            InvoiceId = Guid.NewGuid(),
            Amount = 100m,
            PaymentMethod = "CASH"
        };

        // Act & Assert
        await Assert.ThrowsAsync<_1Rad.Domain.Exceptions.NotFoundException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_InvoiceFromDifferentHospital_ThrowsNotFoundException()
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
        await Assert.ThrowsAsync<_1Rad.Domain.Exceptions.NotFoundException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_AlreadyPaidInvoice_ThrowsConflictException()
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
        await Assert.ThrowsAsync<_1Rad.Domain.Exceptions.ConflictException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_CancelledInvoice_ThrowsBusinessRuleViolation()
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
        await Assert.ThrowsAsync<_1Rad.Domain.Exceptions.BusinessRuleViolationException>(
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

    // ── Review B1 / B6 / B7 (Oct 2026) ─────────────────────────────────────

    [Fact]
    public async Task Handle_StaleRowVersion_IsRefusedWithConflict()
    {
        var invoiceId = Guid.NewGuid();
        Context.Invoices.Add(new Invoice
        {
            Id = invoiceId, InvoiceId = "INV-RV-1", HospitalId = HospitalId,
            GrossAmount = 1000m, TotalAmount = 1000m, Status = "PENDING",
            RowVersion = new byte[] { 0, 0, 0, 0, 0, 0, 0, 2 },
        });
        await Context.SaveChangesAsync();

        await Assert.ThrowsAsync<_1Rad.Domain.Exceptions.ConflictException>(() => _handler.Handle(new CollectPaymentCommand
        {
            InvoiceId = invoiceId, Amount = 500m, PaymentMethod = "CASH",
            RowVersion = Convert.ToBase64String(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }),
        }, CancellationToken.None));
        Assert.Empty(Context.Payments.Where(p => p.InvoiceId == invoiceId));
    }

    [Fact]
    public async Task Handle_PaidAmountComesFromPaymentRows()
    {
        var invoiceId = Guid.NewGuid();
        Context.Invoices.Add(new Invoice
        {
            Id = invoiceId, InvoiceId = "INV-ROWS-1", HospitalId = HospitalId,
            GrossAmount = 1000m, TotalAmount = 1000m, Status = "PARTIAL",
            PaidAmount = 900m, // drifted: only ₹400 was actually recorded
        });
        Context.Payments.Add(new Payment { InvoiceId = invoiceId, HospitalId = HospitalId, Amount = 400m, PaymentMethod = "CASH" });
        await Context.SaveChangesAsync();

        await _handler.Handle(new CollectPaymentCommand { InvoiceId = invoiceId, Amount = 100m, PaymentMethod = "CASH" }, CancellationToken.None);

        var updated = await Context.Invoices.FindAsync(invoiceId);
        Assert.Equal(500m, updated!.PaidAmount);
        Assert.Equal("PARTIAL", updated.Status);
    }

    [Fact]
    public async Task Handle_FrontDeskDiscountAboveLimit_IsRefused()
    {
        MockUserContext.Setup(x => x.RoleIds).Returns(new[] { 5 }); // Receptionist
        var invoiceId = Guid.NewGuid();
        var invoice = new Invoice { Id = invoiceId, InvoiceId = "INV-DISC-1", HospitalId = HospitalId, GrossAmount = 1000m, TotalAmount = 1000m, Status = "PENDING" };
        invoice.Items.Add(new InvoiceItem { InvoiceId = invoiceId, Description = "CT", Amount = 1000m, Quantity = 1 });
        Context.Invoices.Add(invoice);
        await Context.SaveChangesAsync();

        await Assert.ThrowsAsync<_1Rad.Domain.Exceptions.BusinessRuleViolationException>(() => _handler.Handle(new CollectPaymentCommand
        {
            InvoiceId = invoiceId, Amount = 700m, PaymentMethod = "CASH", CentreDiscount = 300m,
        }, CancellationToken.None));
    }
}
