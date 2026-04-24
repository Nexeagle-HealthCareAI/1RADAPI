# Next Steps - Action Plan

## 🎯 Immediate Next Action

**Continue with Finance tests** - You have 85 more Finance tests to implement.

---

## 📋 Step-by-Step Guide

### Step 1: Choose Next Handler to Test

Pick from the remaining Finance handlers (in priority order):

1. ✅ CollectPaymentCommandHandler (11 tests) - **DONE**
2. ✅ GenerateInvoiceCommandHandler (14 tests) - **DONE**
3. ⏳ UpsertServiceChargeCommandHandler (8 tests) - **NEXT**
4. ⏳ DeleteServiceChargeCommandHandler (6 tests)
5. ⏳ GetServiceChargesQueryHandler (5 tests)
6. ⏳ GetInvoicesQueryHandler (10 tests)
7. ⏳ GetFinanceStatsQueryHandler (8 tests)
8. ⏳ SyncLocalStorageInvoicesCommandHandler (8 tests)
9. ⏳ ExportFinanceDataQueryHandler (7 tests)
10. ⏳ GetFinancialMatrixQueryHandler (6 tests)
11. ⏳ GetPendingBillablesQueryHandler (7 tests)
12. ⏳ GetExpensesQueryHandler (10 tests)
13. ⏳ RecordExpenseCommandHandler (10 tests)

---

### Step 2: Find the Handler File

**Recommended Next**: UpsertServiceChargeCommandHandler

Location: `1Rad.Application/Features/Finance/Commands/UpsertServiceCharge/UpsertServiceChargeCommand.cs`

---

### Step 3: Create Test File

Create: `1Rad.UnitTests/Features/Finance/UpsertServiceChargeCommandHandlerTests.cs`

```csharp
using _1Rad.Application.Features.Finance.Commands.UpsertServiceCharge;
using _1Rad.Domain.Entities;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance;

public class UpsertServiceChargeCommandHandlerTests : BaseHandlerTest
{
    private readonly UpsertServiceChargeCommandHandler _handler;

    public UpsertServiceChargeCommandHandlerTests()
    {
        _handler = new UpsertServiceChargeCommandHandler(Context);
    }

    [Fact]
    public async Task Handle_CreateNewServiceCharge_ReturnsId()
    {
        // Arrange
        var command = new UpsertServiceChargeCommand
        {
            ServiceName = "X-Ray Chest",
            Amount = 500m,
            Modality = "X-RAY"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotEqual(Guid.Empty, result);
        
        var serviceCharge = await Context.ServiceCharges.FindAsync(result);
        Assert.NotNull(serviceCharge);
        Assert.Equal("X-Ray Chest", serviceCharge.ServiceName);
        Assert.Equal(500m, serviceCharge.Amount);
        Assert.Equal(HospitalId, serviceCharge.HospitalId);
    }

    [Fact]
    public async Task Handle_UpdateExistingServiceCharge_ReturnsId()
    {
        // Arrange
        var existingId = Guid.NewGuid();
        var existing = new ServiceCharge
        {
            Id = existingId,
            ServiceName = "X-Ray Chest",
            Amount = 500m,
            HospitalId = HospitalId,
            Modality = "X-RAY"
        };

        Context.ServiceCharges.Add(existing);
        await Context.SaveChangesAsync();

        var command = new UpsertServiceChargeCommand
        {
            Id = existingId,
            ServiceName = "X-Ray Chest",
            Amount = 600m, // Updated amount
            Modality = "X-RAY"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.Equal(existingId, result);
        
        var updated = await Context.ServiceCharges.FindAsync(existingId);
        Assert.NotNull(updated);
        Assert.Equal(600m, updated.Amount);
    }

    [Fact]
    public async Task Handle_InvalidAmount_ThrowsArgumentException()
    {
        // Arrange
        var command = new UpsertServiceChargeCommand
        {
            ServiceName = "X-Ray",
            Amount = 0m, // Invalid
            Modality = "X-RAY"
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_NegativeAmount_ThrowsArgumentException()
    {
        // Arrange
        var command = new UpsertServiceChargeCommand
        {
            ServiceName = "X-Ray",
            Amount = -100m,
            Modality = "X-RAY"
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_MissingServiceName_ThrowsArgumentException()
    {
        // Arrange
        var command = new UpsertServiceChargeCommand
        {
            ServiceName = "",
            Amount = 500m,
            Modality = "X-RAY"
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_EmptyHospitalContext_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        MockUserContext.Setup(x => x.HospitalId).Returns(Guid.Empty);

        var command = new UpsertServiceChargeCommand
        {
            ServiceName = "X-Ray",
            Amount = 500m,
            Modality = "X-RAY"
        };

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_InvalidModality_ThrowsArgumentException()
    {
        // Arrange
        var command = new UpsertServiceChargeCommand
        {
            ServiceName = "X-Ray",
            Amount = 500m,
            Modality = "INVALID"
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.Handle(command, CancellationToken.None));
    }

    [Fact]
    public async Task Handle_DifferentHospitalServiceCharge_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var existingId = Guid.NewGuid();
        var existing = new ServiceCharge
        {
            Id = existingId,
            ServiceName = "X-Ray",
            Amount = 500m,
            HospitalId = Guid.NewGuid(), // Different hospital
            Modality = "X-RAY"
        };

        Context.ServiceCharges.Add(existing);
        await Context.SaveChangesAsync();

        var command = new UpsertServiceChargeCommand
        {
            Id = existingId,
            ServiceName = "X-Ray",
            Amount = 600m,
            Modality = "X-RAY"
        };

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _handler.Handle(command, CancellationToken.None));
    }
}
```

---

### Step 4: Run Tests

```bash
dotnet test --filter "FullyQualifiedName~UpsertServiceCharge"
```

---

### Step 5: Verify All Pass

Make sure all 8 tests pass before moving to the next handler.

---

### Step 6: Repeat

Continue with the next handler in the list until all Finance tests are complete.

---

## 📊 Track Your Progress

Update this checklist as you complete each handler:

### Finance Handlers (13 total)
- [x] CollectPaymentCommandHandler (11 tests)
- [x] GenerateInvoiceCommandHandler (14 tests)
- [ ] UpsertServiceChargeCommandHandler (8 tests) ← **START HERE**
- [ ] DeleteServiceChargeCommandHandler (6 tests)
- [ ] GetServiceChargesQueryHandler (5 tests)
- [ ] GetInvoicesQueryHandler (10 tests)
- [ ] GetFinanceStatsQueryHandler (8 tests)
- [ ] SyncLocalStorageInvoicesCommandHandler (8 tests)
- [ ] ExportFinanceDataQueryHandler (7 tests)
- [ ] GetFinancialMatrixQueryHandler (6 tests)
- [ ] GetPendingBillablesQueryHandler (7 tests)
- [ ] GetExpensesQueryHandler (10 tests)
- [ ] RecordExpenseCommandHandler (10 tests)

**Progress**: 2/13 handlers (15%) → Target: 13/13 (100%)

---

## 💡 Tips for Success

1. **One Handler at a Time** - Don't try to do everything at once
2. **Copy Existing Tests** - Use CollectPaymentCommandHandlerTests as template
3. **Run Tests Frequently** - After every 2-3 tests
4. **Fix Failures Immediately** - Don't let them accumulate
5. **Take Breaks** - Testing can be tedious, pace yourself

---

## 🎯 Daily Goals

### Realistic Pace (1-2 hours/day)
- **Day 1**: UpsertServiceChargeCommandHandler (8 tests)
- **Day 2**: DeleteServiceChargeCommandHandler (6 tests) + GetServiceChargesQueryHandler (5 tests)
- **Day 3**: GetInvoicesQueryHandler (10 tests)
- **Day 4**: GetFinanceStatsQueryHandler (8 tests)
- **Day 5**: SyncLocalStorageInvoicesCommandHandler (8 tests)
- **Day 6**: ExportFinanceDataQueryHandler (7 tests) + GetFinancialMatrixQueryHandler (6 tests)
- **Day 7**: GetPendingBillablesQueryHandler (7 tests)
- **Day 8**: GetExpensesQueryHandler (10 tests)
- **Day 9**: RecordExpenseCommandHandler (10 tests)
- **Day 10**: Buffer/catch-up day

**Result**: All Finance tests complete in ~2 weeks!

---

## 🚀 After Finance is Complete

Move to the next priority:

1. **Appointments** (48 tests) - Core workflow
2. **Patients** (22 tests) - Foundation
3. **Intelligence** (15 tests) - Analytics
4. **Reporting** (34 tests) - Clinical workflow
5. **Study** (30 tests) - DICOM handling
6. **Referrers** (25 tests) - Business intelligence
7. **Hospitals** (16 remaining tests) - Configuration
8. **Prescription** (26 tests) - Doctor workflow
9. **Health** (3 tests) - Monitoring

---

## 📞 Resources

- **COMPLETE_API_TEST_CATALOG.md** - See all test cases for each endpoint
- **CollectPaymentCommandHandlerTests.cs** - Reference implementation
- **BaseHandlerTest.cs** - Base class documentation
- **QUICK_TEST_GENERATOR_GUIDE.md** - Detailed how-to guide

---

## 🎊 Celebrate Wins!

- ✅ Every handler completed
- ✅ Every 10 tests passing
- ✅ Every feature area completed
- ✅ Reaching 50%, 60%, 70%, 80% coverage

**You've got this!** 💪

Start with UpsertServiceChargeCommandHandler and keep the momentum going!
