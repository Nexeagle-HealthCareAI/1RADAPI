# Quick Test Generator Guide

## Overview
This guide helps you quickly create comprehensive unit tests for all APIs to achieve 80%+ test coverage.

## Test Template Structure

Every command/query handler test should follow this structure:

```csharp
using _1Rad.Application.Features.[Feature].[Commands|Queries].[HandlerName];
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.UnitTests.Helpers;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace _1Rad.UnitTests.Features.[Feature];

public class [HandlerName]Tests
{
    private readonly Mock<IApplicationDbContext> _mockContext;
    private readonly Mock<IUserContext> _mockUserContext;
    // Add Mock<DbSet<T>> for each entity used
    private readonly [HandlerName] _handler;
    private readonly Guid _hospitalId = Guid.NewGuid();

    public [HandlerName]Tests()
    {
        // Setup mocks
        _mockContext = new Mock<IApplicationDbContext>();
        _mockUserContext = new Mock<IUserContext>();
        
        _mockUserContext.Setup(x => x.HospitalId).Returns(_hospitalId);
        _mockContext.Setup(x => x.UserContext).Returns(_mockUserContext.Object);
        
        _handler = new [HandlerName](_mockContext.Object);
    }

    // Tests go here
}
```

## Essential Test Cases for Every Handler

### 1. Happy Path Tests
```csharp
[Fact]
public async Task Handle_ValidInput_ReturnsExpectedResult()
{
    // Arrange
    // Setup test data
    
    // Act
    var result = await _handler.Handle(command, CancellationToken.None);
    
    // Assert
    Assert.NotNull(result);
    // Add specific assertions
}
```

### 2. Authorization Tests
```csharp
[Fact]
public async Task Handle_EmptyHospitalContext_ThrowsUnauthorizedAccessException()
{
    // Arrange
    _mockUserContext.Setup(x => x.HospitalId).Returns(Guid.Empty);
    
    // Act & Assert
    await Assert.ThrowsAsync<UnauthorizedAccessException>(
        () => _handler.Handle(command, CancellationToken.None));
}

[Fact]
public async Task Handle_DifferentHospital_ThrowsUnauthorizedAccessException()
{
    // Test accessing resources from different hospital
}
```

### 3. Validation Tests
```csharp
[Fact]
public async Task Handle_InvalidInput_ThrowsArgumentException()
{
    // Test each validation rule
}

[Fact]
public async Task Handle_NullInput_ThrowsArgumentNullException()
{
    // Test null inputs
}
```

### 4. Not Found Tests
```csharp
[Fact]
public async Task Handle_EntityNotFound_ThrowsKeyNotFoundException()
{
    // Test when entity doesn't exist
}
```

### 5. Business Logic Tests
```csharp
[Fact]
public async Task Handle_InvalidBusinessRule_ThrowsInvalidOperationException()
{
    // Test business rule violations
}
```

## Mock DbSet Helper Method

Add this to every test class:

```csharp
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
}
```

## Test Naming Convention

Use descriptive names that follow this pattern:
```
[MethodName]_[Scenario]_[ExpectedResult]
```

Examples:
- `Handle_ValidPayment_UpdatesInvoiceStatus`
- `Handle_EmptyHospitalContext_ThrowsUnauthorizedAccessException`
- `Handle_PatientNotFound_ThrowsKeyNotFoundException`

## Running Tests

```bash
# Run all tests
dotnet test

# Run specific test class
dotnet test --filter "FullyQualifiedName~CollectPaymentCommandHandlerTests"

# Run with coverage
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=opencover
```

## Coverage Goals

- **Minimum**: 80% line coverage
- **Target**: 90% line coverage
- **Commands**: 85%+ coverage (critical business logic)
- **Queries**: 75%+ coverage (data retrieval)

## Priority Order for Test Creation

1. ✅ Finance Commands (Done: CollectPayment, GenerateInvoice)
2. Finance Queries
3. Appointments Commands
4. Appointments Queries
5. Patients Commands
6. Patients Queries
7. Referrers Commands
8. Referrers Queries

## Quick Checklist for Each Handler

- [ ] Happy path test
- [ ] Empty hospital context test
- [ ] Invalid input tests (for each validation)
- [ ] Entity not found test
- [ ] Different hospital test (if applicable)
- [ ] Business rule violation tests
- [ ] Edge cases (null, empty, boundary values)
- [ ] Multiple scenarios (if applicable)

## Example: Complete Test Class

See `CollectPaymentCommandHandlerTests.cs` and `GenerateInvoiceCommandHandlerTests.cs` for complete examples.

## Tips for Fast Test Creation

1. Copy an existing test class as template
2. Replace handler name and entities
3. Identify all validation rules from handler code
4. Create one test per validation rule
5. Add happy path and edge cases
6. Run tests frequently to catch issues early
7. Use test coverage tools to identify gaps

## Common Patterns

### Testing Queries with Filters
```csharp
[Theory]
[InlineData("PENDING")]
[InlineData("PAID")]
[InlineData("CANCELLED")]
public async Task Handle_FilterByStatus_ReturnsFilteredResults(string status)
{
    // Test filtering logic
}
```

### Testing Pagination
```csharp
[Fact]
public async Task Handle_WithPagination_ReturnsCorrectPage()
{
    // Test pagination logic
}
```

### Testing Search
```csharp
[Fact]
public async Task Handle_WithSearchQuery_ReturnsMatchingResults()
{
    // Test search logic
}
```

## Next Steps

1. Review existing tests (CollectPayment, GenerateInvoice)
2. Use them as templates for similar handlers
3. Create tests systematically feature by feature
4. Run coverage reports to track progress
5. Aim for 80%+ coverage before moving to next feature
