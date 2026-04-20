using _1Rad.Application.Features.Auth.Commands.VerifyResetCode;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Enums;
using _1Rad.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace _1Rad.UnitTests.Features.Auth;

public class VerifyResetCodeCommandHandlerTests
{
    private readonly Mock<IPasswordHasher> _hasherMock;
    private readonly Mock<IJwtProvider> _jwtProviderMock;
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<IUserContext> _userContextMock;
    private readonly ApplicationDbContext _context;

    public VerifyResetCodeCommandHandlerTests()
    {
        _hasherMock = new Mock<IPasswordHasher>();
        _jwtProviderMock = new Mock<IJwtProvider>();
        _publisherMock = new Mock<IPublisher>();
        _userContextMock = new Mock<IUserContext>();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options, _publisherMock.Object, _userContextMock.Object);
    }

    [Fact]
    public async Task Handle_WithValidCode_ShouldReturnResetToken()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            UserId = userId,
            Email = "user@example.com",
            Mobile = "+1234567890",
            FullName = "Test User",
            PasswordHash = "hashed_password",
            Status = UserStatus.Active
        };
        _context.Users.Add(user);

        var otpVerification = new OTPVerification
        {
            Identifier = "user@example.com",
            CodeHash = "hashed_code",
            Purpose = "PasswordReset",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            IsUsed = false
        };
        _context.OTPVerifications.Add(otpVerification);
        await _context.SaveChangesAsync();

        _hasherMock.Setup(x => x.Verify("123456", "hashed_code")).Returns(true);
        _jwtProviderMock.Setup(x => x.GenerateResetToken(userId)).Returns("reset_token_xyz");

        var handler = new VerifyResetCodeCommandHandler(_context, _hasherMock.Object, _jwtProviderMock.Object);
        var command = new VerifyResetCodeCommand("user@example.com", "123456");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.ResetToken.Should().Be("reset_token_xyz");
        result.Error.Should().BeNull();

        var updatedOtp = await _context.OTPVerifications.FirstAsync(o => o.Id == otpVerification.Id);
        updatedOtp.IsUsed.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WithInvalidCode_ShouldReturnError()
    {
        // Arrange
        var user = new User
        {
            UserId = Guid.NewGuid(),
            Email = "user@example.com",
            Mobile = "+1234567890",
            FullName = "Test User",
            PasswordHash = "hashed_password",
            Status = UserStatus.Active
        };
        _context.Users.Add(user);

        var otpVerification = new OTPVerification
        {
            Identifier = "user@example.com",
            CodeHash = "hashed_code",
            Purpose = "PasswordReset",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            IsUsed = false
        };
        _context.OTPVerifications.Add(otpVerification);
        await _context.SaveChangesAsync();

        _hasherMock.Setup(x => x.Verify("wrong_code", "hashed_code")).Returns(false);

        var handler = new VerifyResetCodeCommandHandler(_context, _hasherMock.Object, _jwtProviderMock.Object);
        var command = new VerifyResetCodeCommand("user@example.com", "wrong_code");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ResetToken.Should().BeNull();
        result.Error.Should().Be("Invalid verification code.");
    }

    [Fact]
    public async Task Handle_WithExpiredCode_ShouldReturnError()
    {
        // Arrange
        var user = new User
        {
            UserId = Guid.NewGuid(),
            Email = "user@example.com",
            Mobile = "+1234567890",
            FullName = "Test User",
            PasswordHash = "hashed_password",
            Status = UserStatus.Active
        };
        _context.Users.Add(user);

        var otpVerification = new OTPVerification
        {
            Identifier = "user@example.com",
            CodeHash = "hashed_code",
            Purpose = "PasswordReset",
            ExpiresAt = DateTime.UtcNow.AddMinutes(-5), // Expired
            IsUsed = false
        };
        _context.OTPVerifications.Add(otpVerification);
        await _context.SaveChangesAsync();

        var handler = new VerifyResetCodeCommandHandler(_context, _hasherMock.Object, _jwtProviderMock.Object);
        var command = new VerifyResetCodeCommand("user@example.com", "123456");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("The verification code is invalid or has expired.");
    }

    [Fact]
    public async Task Handle_WithAlreadyUsedCode_ShouldReturnError()
    {
        // Arrange
        var user = new User
        {
            UserId = Guid.NewGuid(),
            Email = "user@example.com",
            Mobile = "+1234567890",
            FullName = "Test User",
            PasswordHash = "hashed_password",
            Status = UserStatus.Active
        };
        _context.Users.Add(user);

        var otpVerification = new OTPVerification
        {
            Identifier = "user@example.com",
            CodeHash = "hashed_code",
            Purpose = "PasswordReset",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            IsUsed = true // Already used
        };
        _context.OTPVerifications.Add(otpVerification);
        await _context.SaveChangesAsync();

        var handler = new VerifyResetCodeCommandHandler(_context, _hasherMock.Object, _jwtProviderMock.Object);
        var command = new VerifyResetCodeCommand("user@example.com", "123456");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("The verification code is invalid or has expired.");
    }

    [Fact]
    public async Task Handle_WithNonExistentUser_ShouldReturnError()
    {
        // Arrange
        var otpVerification = new OTPVerification
        {
            Identifier = "nonexistent@example.com",
            CodeHash = "hashed_code",
            Purpose = "PasswordReset",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            IsUsed = false
        };
        _context.OTPVerifications.Add(otpVerification);
        await _context.SaveChangesAsync();

        _hasherMock.Setup(x => x.Verify("123456", "hashed_code")).Returns(true);

        var handler = new VerifyResetCodeCommandHandler(_context, _hasherMock.Object, _jwtProviderMock.Object);
        var command = new VerifyResetCodeCommand("nonexistent@example.com", "123456");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("User synchronization error.");
    }
}
