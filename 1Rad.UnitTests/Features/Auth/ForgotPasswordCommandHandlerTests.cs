using _1Rad.Application.Features.Auth.Commands.ForgotPassword;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Enums;
using _1Rad.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace _1Rad.UnitTests.Features.Auth;

public class ForgotPasswordCommandHandlerTests
{
    private readonly Mock<IOtpService> _otpServiceMock;
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<IUserContext> _userContextMock;
    private readonly Mock<ILogger<ForgotPasswordCommandHandler>> _loggerMock;
    private readonly ApplicationDbContext _context;

    public ForgotPasswordCommandHandlerTests()
    {
        _otpServiceMock = new Mock<IOtpService>();
        _publisherMock = new Mock<IPublisher>();
        _userContextMock = new Mock<IUserContext>();
        _loggerMock = new Mock<ILogger<ForgotPasswordCommandHandler>>();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options, _publisherMock.Object, _userContextMock.Object);
    }

    [Fact]
    public async Task Handle_WithValidEmail_ShouldSendOtpAndReturnSuccess()
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
        await _context.SaveChangesAsync();

        _otpServiceMock.Setup(x => x.GenerateAndSendOtpAsync(It.IsAny<string>(), "PasswordReset"))
            .ReturnsAsync("123456");

        var handler = new ForgotPasswordCommandHandler(_context, _otpServiceMock.Object, _loggerMock.Object);
        var command = new ForgotPasswordCommand("user@example.com");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Message.Should().Contain("verification code");
        _otpServiceMock.Verify(x => x.GenerateAndSendOtpAsync("user@example.com", "PasswordReset"), Times.Once);
    }

    [Fact]
    public async Task Handle_WithValidMobile_ShouldSendOtpAndReturnSuccess()
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
        await _context.SaveChangesAsync();

        _otpServiceMock.Setup(x => x.GenerateAndSendOtpAsync(It.IsAny<string>(), "PasswordReset"))
            .ReturnsAsync("123456");

        var handler = new ForgotPasswordCommandHandler(_context, _otpServiceMock.Object, _loggerMock.Object);
        var command = new ForgotPasswordCommand("+1234567890");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Message.Should().Contain("verification code");
        _otpServiceMock.Verify(x => x.GenerateAndSendOtpAsync("+1234567890", "PasswordReset"), Times.Once);
    }

    [Fact]
    public async Task Handle_WithNonExistentUser_ShouldReturnSuccessToPreventEnumeration()
    {
        // Arrange
        _otpServiceMock.Setup(x => x.GenerateAndSendOtpAsync(It.IsAny<string>(), "PasswordReset"))
            .ReturnsAsync("123456");

        var handler = new ForgotPasswordCommandHandler(_context, _otpServiceMock.Object, _loggerMock.Object);
        var command = new ForgotPasswordCommand("nonexistent@example.com");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Message.Should().Contain("If your account exists");
        _otpServiceMock.Verify(x => x.GenerateAndSendOtpAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenOtpServiceFails_ShouldThrowException()
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
        await _context.SaveChangesAsync();

        _otpServiceMock.Setup(x => x.GenerateAndSendOtpAsync(It.IsAny<string>(), "PasswordReset"))
            .ThrowsAsync(new Exception("SMS service unavailable"));

        var handler = new ForgotPasswordCommandHandler(_context, _otpServiceMock.Object, _loggerMock.Object);
        var command = new ForgotPasswordCommand("user@example.com");

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() => handler.Handle(command, CancellationToken.None));
    }
}
