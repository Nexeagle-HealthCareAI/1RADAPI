# Unit Test Implementation Guide for 1Rad API

This guide provides comprehensive test coverage for all API endpoints and command/query handlers.

## Test Structure

```
1Rad.UnitTests/
├── Features/
│   ├── Auth/                    # Authentication tests
│   ├── Personnel/               # Personnel management tests
│   └── Hospitals/               # Hospital management tests
├── Middleware/                  # Middleware tests
└── Services/                    # Service tests (to be created)
```

## Testing Framework

- **Test Framework**: xUnit
- **Mocking**: Moq
- **Assertions**: FluentAssertions
- **Database**: EF Core InMemory

## Test Coverage Summary

### ✅ Completed Tests

#### Auth Feature
1. **SendOTPCommandHandlerTests** - OTP generation and sending
2. **VerifyOTPCommandHandlerTests** - OTP verification
3. **IdentitySetupCommandHandlerTests** - User identity setup
4. **LoginCommandHandlerTests** - User login
5. **RefreshTokenCommandHandlerTests** - Token refresh
6. **SwitchContextCommandHandlerTests** - Hospital context switching
7. **DeployInfrastructureCommandHandlerTests** - Infrastructure deployment
8. **ForgotPasswordCommandHandlerTests** ✨ NEW - Password recovery initiation
9. **VerifyResetCodeCommandHandlerTests** ✨ NEW - Reset code verification
10. **ResetPasswordCommandHandlerTests** ✨ NEW - Password reset

### 🔧 Tests Requiring Fixes

#### Personnel Feature
1. **RegisterStaffCommandHandlerTests** ✨ NEW - Needs signature fixes
2. **UpdateStaffCommandHandlerTests** ✨ NEW - Needs signature fixes
3. **RemoveStaffCommandHandlerTests** ✨ NEW - Needs signature fixes
4. **GetHospitalPersonnelQueryHandlerTests** ✨ NEW - Needs signature fixes

#### Hospital Feature
5. **UpdateHospitalDetailsCommandHandlerTests** ✨ NEW - Needs signature fixes
6. **GetHospitalDetailsQueryHandlerTests** ✨ NEW - Needs signature fixes

## Command/Query Signatures Reference

### Personnel Commands

```csharp
// RegisterStaffCommand
public record RegisterStaffCommand(
    Guid HospitalId,
    string FullName,
    string Email,
    string Mobile,
    string Password,
    List<string> RoleNames,  // Note: RoleNames, not RoleIds
    string? Specialization = null,
    string? Degree = null,
    string? LicenseNo = null
) : IRequest<(Guid UserId, string? Error)>;

// UpdateStaffCommand
public record UpdateStaffCommand(
    Guid UserId,
    Guid HospitalId,
    string FullName,
    List<string> RoleNames,  // Note: RoleNames, not RoleIds
    string? Specialization = null,
    string? Degree = null,
    string? LicenseNo = null
) : IRequest<(bool Success, string? Error)>;

// RemoveStaffCommand
public record RemoveStaffCommand(
    Guid UserId,
    Guid HospitalId
) : IRequest<(bool Success, string? Error)>;

// GetHospitalPersonnelQuery
public record GetHospitalPersonnelQuery(
    Guid HospitalId
) : IRequest<List<PersonnelDto>>;
```

### Hospital Commands/Queries

```csharp
// UpdateHospitalDetailsCommand
public record UpdateHospitalDetailsCommand(
    Guid HospitalId,
    string HospitalName,
    string HospitalAddress,
    string? GSTIN = null,
    string? ContactNumber = null,
    string? Email = null,
    string? Website = null
) : IRequest<(bool Success, string? Error)>;

// GetHospitalDetailsQuery
public record GetHospitalDetailsQuery(
    Guid HospitalId
) : IRequest<HospitalDetailsDto>;
```

### Auth Commands

```csharp
// ForgotPasswordCommand
public record ForgotPasswordCommand(
    string Identifier  // Email or Mobile
) : IRequest<(bool Success, string? Message)>;

// VerifyResetCodeCommand
public record VerifyResetCodeCommand(
    string Identifier,
    string Code
) : IRequest<(bool Success, string? ResetToken, string? Error)>;

// ResetPasswordCommand
public record ResetPasswordCommand(
    string ResetToken,
    string NewPassword
) : IRequest<(bool Success, string? Error)>;
```

## Test Patterns

### 1. Basic Test Structure

```csharp
public class CommandHandlerTests
{
    private readonly Mock<IDependency> _dependencyMock;
    private readonly ApplicationDbContext _context;

    public CommandHandlerTests()
    {
        // Setup mocks
        _dependencyMock = new Mock<IDependency>();
        
        // Setup InMemory database
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new ApplicationDbContext(options, publisherMock, userContextMock);
    }

    [Fact]
    public async Task Handle_WithValidData_ShouldSucceed()
    {
        // Arrange
        // ... setup test data
        
        // Act
        var result = await handler.Handle(command, CancellationToken.None);
        
        // Assert
        result.Success.Should().BeTrue();
    }
}
```

### 2. Test Scenarios to Cover

For each command/query handler, test:

1. **Happy Path** - Valid data returns success
2. **Validation Failures** - Invalid data returns appropriate errors
3. **Not Found** - Non-existent entities return not found errors
4. **Authorization** - Users can only access their hospital's data
5. **Edge Cases** - Null values, empty lists, boundary conditions
6. **Concurrency** - Multiple operations don't conflict
7. **Side Effects** - Domain events are raised, related entities are updated

### 3. Common Test Scenarios

#### Authentication Tests
- ✅ Valid credentials return tokens
- ✅ Invalid credentials return error
- ✅ Expired tokens are rejected
- ✅ Token refresh works correctly
- ✅ OTP generation and verification
- ✅ Password reset flow

#### Personnel Tests
- ⚠️ Register new staff member
- ⚠️ Register existing user to new hospital
- ⚠️ Update staff details and roles
- ⚠️ Remove staff from hospital
- ⚠️ List all personnel for hospital
- ⚠️ Cannot access staff from other hospitals

#### Hospital Tests
- ⚠️ Update hospital details
- ⚠️ Get hospital details
- ⚠️ Cannot update other hospitals

## Running Tests

```bash
# Run all tests
dotnet test

# Run specific test class
dotnet test --filter "FullyQualifiedName~LoginCommandHandlerTests"

# Run tests with coverage
dotnet test /p:CollectCoverage=true

# Run tests in watch mode
dotnet watch test
```

## Test Data Builders

### User Builder
```csharp
public static User CreateTestUser(
    string email = "test@example.com",
    string mobile = "+1234567890",
    UserStatus status = UserStatus.Active)
{
    return new User
    {
        UserId = Guid.NewGuid(),
        FullName = "Test User",
        Email = email,
        Mobile = mobile,
        PasswordHash = "hashed_password",
        Status = status,
        IsVerified = true
    };
}
```

### Hospital Builder
```csharp
public static Hospital CreateTestHospital(string name = "Test Hospital")
{
    return new Hospital
    {
        HospitalId = Guid.NewGuid(),
        HospitalName = name,
        HospitalAddress = "123 Test St",
        Status = "Active"
    };
}
```

## Mocking Guidelines

### 1. Password Hasher
```csharp
_hasherMock.Setup(x => x.Hash(It.IsAny<string>())).Returns("hashed_password");
_hasherMock.Setup(x => x.Verify("correct_password", "hashed_password")).Returns(true);
_hasherMock.Setup(x => x.Verify("wrong_password", "hashed_password")).Returns(false);
```

### 2. JWT Provider
```csharp
_jwtProviderMock.Setup(x => x.GenerateContextualToken(
    It.IsAny<User>(),
    It.IsAny<UserHospitalMapping>(),
    It.IsAny<IEnumerable<Guid>>()))
    .Returns("access_token");

_jwtProviderMock.Setup(x => x.GenerateRefreshToken())
    .Returns("refresh_token");
```

### 3. User Context
```csharp
_userContextMock.Setup(x => x.UserId).Returns(userId);
_userContextMock.Setup(x => x.HospitalId).Returns(hospitalId);
_userContextMock.Setup(x => x.RoleId).Returns(roleId);
```

### 4. OTP Service
```csharp
_otpServiceMock.Setup(x => x.GenerateAndSendOtpAsync(
    It.IsAny<string>(),
    It.IsAny<string>()))
    .ReturnsAsync("123456");
```

## Integration Test Considerations

While unit tests mock dependencies, consider creating integration tests for:

1. **End-to-End Flows** - Complete user journeys
2. **Database Interactions** - Real database operations
3. **External Services** - SMS, Email services
4. **API Controllers** - HTTP request/response testing

## Test Naming Conventions

```
Handle_With{Condition}_Should{ExpectedResult}

Examples:
- Handle_WithValidCredentials_ShouldReturnAccessToken
- Handle_WithInvalidPassword_ShouldReturnError
- Handle_WithExpiredToken_ShouldReturnUnauthorized
- Handle_WithNonExistentUser_ShouldReturnNotFound
```

## Assertion Patterns

```csharp
// Success assertions
result.Success.Should().BeTrue();
result.Error.Should().BeNull();

// Failure assertions
result.Success.Should().BeFalse();
result.Error.Should().Contain("expected error message");

// Entity assertions
user.Should().NotBeNull();
user!.FullName.Should().Be("Expected Name");
user.Status.Should().Be(UserStatus.Active);

// Collection assertions
result.Should().HaveCount(2);
result.Should().Contain(x => x.Name == "Test");
result.Should().BeEmpty();

// Mock verification
_mockService.Verify(x => x.Method(It.IsAny<string>()), Times.Once);
_mockService.Verify(x => x.Method(It.IsAny<string>()), Times.Never);
```

## Next Steps

1. ✅ Fix command/query signatures in new tests
2. ✅ Add test data builders for common entities
3. ✅ Create integration tests for critical flows
4. ✅ Add performance tests for query handlers
5. ✅ Implement test coverage reporting
6. ✅ Add mutation testing for test quality

## Test Coverage Goals

- **Unit Tests**: 80%+ code coverage
- **Integration Tests**: All critical user flows
- **E2E Tests**: Main user journeys
- **Performance Tests**: Query optimization validation

## Legend

- ✅ Completed and passing
- ⚠️ Created but needs fixes
- ❌ Not yet created
- 🔧 In progress
