# Improved Error Handling Examples

## Overview

This document demonstrates how to use the new error handling system in the 1Rad API.

## New Components

### 1. Custom Exceptions
- `DomainException` - Base exception for all domain errors
- `NotFoundException` - Resource not found (404)
- `ValidationException` - Validation errors (400)
- `UnauthorizedException` - Authentication required (401)
- `ForbiddenException` - Insufficient permissions (403)
- `ConflictException` - Resource conflicts (409)
- `BusinessRuleViolationException` - Business rule violations (422)
- `ExternalServiceException` - External service failures (502)

### 2. Error Codes & Messages
- `ErrorCodes` - Centralized error code constants
- `ErrorMessages` - User-friendly error messages

### 3. Response Models
- `ApiResponse<T>` - Standardized API response wrapper
- `Result<T>` - Result pattern for command handlers

## Usage Examples

### Example 1: Using Custom Exceptions in Command Handlers

```csharp
using _1Rad.Domain.Exceptions;
using _1Rad.Domain.Constants;

public class LoginCommandHandler : IRequestHandler<LoginCommand, LoginResponse>
{
    public async Task<LoginResponse> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        // Find user
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Email == request.Identifier || u.Mobile == request.Identifier, cancellationToken);

        // Throw NotFoundException instead of returning error
        if (user == null)
        {
            throw new NotFoundException(
                ErrorMessages.AUTH_USER_NOT_FOUND
            );
        }

        // Check user status
        if (user.Status != UserStatus.Active)
        {
            throw new BusinessRuleViolationException(
                ErrorMessages.AUTH_ACCOUNT_INACTIVE,
                ErrorCodes.AUTH_ACCOUNT_INACTIVE,
                new Dictionary<string, object>
                {
                    { "userId", user.UserId },
                    { "status", user.Status.ToString() }
                }
            );
        }

        // Verify password
        if (!_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            throw new UnauthorizedException(
                ErrorMessages.AUTH_INVALID_CREDENTIALS,
                ErrorCodes.AUTH_INVALID_CREDENTIALS
            );
        }

        // Check hospital mappings
        if (!user.HospitalMappings.Any())
        {
            throw new BusinessRuleViolationException(
                ErrorMessages.AUTH_NO_HOSPITAL_ACCESS,
                ErrorCodes.AUTH_NO_HOSPITAL_ACCESS
            );
        }

        // Success - return response
        return new LoginResponse
        {
            Success = true,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            UserProfile = userProfile
        };
    }
}
```

### Example 2: Using Result Pattern

```csharp
using _1Rad.Application.Common.Models;
using _1Rad.Domain.Constants;

public class RegisterStaffCommandHandler : IRequestHandler<RegisterStaffCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(RegisterStaffCommand request, CancellationToken cancellationToken)
    {
        // Validate hospital
        var hospital = await _context.Hospitals.FindAsync(request.HospitalId);
        if (hospital == null)
        {
            return Result.Failure<Guid>(
                ErrorMessages.HOSPITAL_NOT_FOUND,
                ErrorCodes.HOSPITAL_NOT_FOUND
            );
        }

        // Check for existing user
        var existingUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email == request.Email || u.Mobile == request.Mobile);

        if (existingUser != null)
        {
            // Check if already registered at this hospital
            var existingMapping = await _context.UserHospitalMappings
                .AnyAsync(m => m.UserId == existingUser.UserId && m.HospitalId == request.HospitalId);

            if (existingMapping)
            {
                return Result.Failure<Guid>(
                    ErrorMessages.PERSONNEL_ALREADY_REGISTERED,
                    ErrorCodes.PERSONNEL_ALREADY_REGISTERED
                );
            }
        }

        // Create user and mapping
        var user = new User { /* ... */ };
        _context.Users.Add(user);
        await _context.SaveChangesAsync(cancellationToken);

        return Result.Success(user.UserId);
    }
}
```

### Example 3: Controller Using ApiResponse

```csharp
[ApiController]
[Route("api/v1/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IMediator _mediator;

    [HttpPost("login")]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Login([FromBody] LoginCommand command)
    {
        try
        {
            var result = await _mediator.Send(command);
            return Ok(ApiResponse<LoginResponse>.SuccessResponse(result));
        }
        catch (DomainException ex)
        {
            return StatusCode(ex.StatusCode, ApiResponse<LoginResponse>.ErrorResponse(ex.Message, ex.ErrorCode));
        }
    }

    [HttpPost("register-staff")]
    public async Task<ActionResult<ApiResponse<Guid>>> RegisterStaff([FromBody] RegisterStaffCommand command)
    {
        var result = await _mediator.Send(command);

        if (result.IsFailure)
        {
            return BadRequest(ApiResponse<Guid>.ErrorResponse(result.Error!, result.ErrorCode!));
        }

        return Ok(ApiResponse<Guid>.SuccessResponse(result.Value!));
    }
}
```

### Example 4: External Service Error Handling

```csharp
public class WhatsAppSmsService : ISmsService
{
    public async Task SendOtpAsync(string mobile, string otp)
    {
        try
        {
            var response = await _httpClient.PostAsync(/* ... */);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new ExternalServiceException(
                    "WhatsApp",
                    $"Failed to send OTP. Status: {response.StatusCode}",
                    new Exception(error)
                );
            }
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalServiceException(
                "WhatsApp",
                "Network error while sending OTP",
                ex
            );
        }
    }
}
```

### Example 5: Validation Exception

```csharp
public class RegisterStaffCommandValidator : AbstractValidator<RegisterStaffCommand>
{
    public RegisterStaffCommandValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Invalid email format.");

        RuleFor(x => x.Mobile)
            .NotEmpty().WithMessage("Mobile number is required.")
            .Matches(@"^\+?[1-9]\d{1,14}$").WithMessage("Invalid mobile number format.");

        RuleFor(x => x.RoleNames)
            .NotEmpty().WithMessage("At least one role must be assigned.");
    }
}
```

## Error Response Format

### Success Response
```json
{
  "success": true,
  "data": {
    "userId": "123e4567-e89b-12d3-a456-426614174000",
    "fullName": "Dr. John Doe",
    "email": "john.doe@hospital.com"
  },
  "timestamp": "2026-04-20T10:30:00Z"
}
```

### Error Response
```json
{
  "success": false,
  "error": "We couldn't find an account with those credentials. Please check and try again.",
  "errorCode": "AUTH_USER_NOT_FOUND",
  "timestamp": "2026-04-20T10:30:00Z"
}
```

### Validation Error Response
```json
{
  "success": false,
  "error": "One or more validation errors occurred.",
  "errorCode": "VALIDATION_ERROR",
  "errors": {
    "email": ["Email is required.", "Invalid email format."],
    "mobile": ["Mobile number is required."]
  },
  "timestamp": "2026-04-20T10:30:00Z"
}
```

### Domain Exception Response
```json
{
  "success": false,
  "error": "Your account is currently inactive. Please contact your administrator for assistance.",
  "errorCode": "AUTH_ACCOUNT_INACTIVE",
  "additionalData": {
    "userId": "123e4567-e89b-12d3-a456-426614174000",
    "status": "Inactive"
  },
  "timestamp": "2026-04-20T10:30:00Z"
}
```

## Migration Guide

### Before (Old Pattern)
```csharp
if (user == null)
{
    return new LoginResponse
    {
        Success = false,
        Error = "User not found",
        ErrorCode = "USER_NOT_FOUND"
    };
}
```

### After (New Pattern - Option 1: Exceptions)
```csharp
if (user == null)
{
    throw new NotFoundException(
        ErrorMessages.AUTH_USER_NOT_FOUND
    );
}
```

### After (New Pattern - Option 2: Result)
```csharp
if (user == null)
{
    return Result.Failure<LoginResponse>(
        ErrorMessages.AUTH_USER_NOT_FOUND,
        ErrorCodes.AUTH_USER_NOT_FOUND
    );
}
```

## Benefits

1. **Consistency**: All errors follow the same format
2. **Type Safety**: Compile-time checking of error codes
3. **User-Friendly**: Centralized, clear error messages
4. **Maintainability**: Easy to update error messages
5. **Debugging**: Better error tracking with error codes
6. **Internationalization**: Easy to add multi-language support
7. **Documentation**: Self-documenting error codes

## Best Practices

1. **Use specific exceptions** for different error types
2. **Include error codes** for all errors
3. **Provide context** in additionalData when helpful
4. **Log errors** appropriately based on severity
5. **Don't expose sensitive information** in error messages
6. **Use user-friendly messages** from ErrorMessages class
7. **Handle external service errors** gracefully
8. **Validate input** early with FluentValidation
9. **Use Result pattern** for expected failures
10. **Use Exceptions** for unexpected failures

## Testing Error Handling

```csharp
[Fact]
public async Task Handle_WithNonExistentUser_ShouldThrowNotFoundException()
{
    // Arrange
    var command = new LoginCommand("nonexistent@example.com", "password");

    // Act & Assert
    var exception = await Assert.ThrowsAsync<NotFoundException>(
        () => _handler.Handle(command, CancellationToken.None)
    );

    exception.Message.Should().Contain("couldn't find an account");
    exception.ErrorCode.Should().Be(ErrorCodes.AUTH_USER_NOT_FOUND);
    exception.StatusCode.Should().Be(404);
}
```
