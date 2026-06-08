using _1Rad.Application.Features.Auth.Commands.VerifyOTP;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace _1Rad.UnitTests.Features.Auth;

public class VerifyOTPCommandHandlerTests
{
    private readonly Mock<IPasswordHasher> _hasherMock;
    private readonly Mock<IJwtProvider> _jwtMock;
    private readonly Mock<IActiveSessionCache> _sessionCacheMock;
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<IUserContext> _userContextMock;
    private readonly Mock<ILogger<VerifyOTPCommandHandler>> _loggerMock;
    private readonly ApplicationDbContext _context;

    public VerifyOTPCommandHandlerTests()
    {
        _hasherMock = new Mock<IPasswordHasher>();
        _jwtMock = new Mock<IJwtProvider>();
        _sessionCacheMock = new Mock<IActiveSessionCache>();
        _publisherMock = new Mock<IPublisher>();
        _userContextMock = new Mock<IUserContext>();
        _loggerMock = new Mock<ILogger<VerifyOTPCommandHandler>>();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options, _publisherMock.Object, _userContextMock.Object);
    }

    [Fact]
    public async Task Handle_WithValidOtp_ShouldReturnToken()
    {
        // Arrange
        var mobile = "9876543210";
        var code = "123456";
        var hash = "hashed_code";

        _context.OTPVerifications.Add(new OTPVerification
        {
            Identifier = mobile,
            CodeHash = hash,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            IsUsed = false
        });
        await _context.SaveChangesAsync();

        _hasherMock.Setup(x => x.Verify(code, hash)).Returns(true);
        _jwtMock.Setup(x => x.GenerateInitiationToken(mobile, null)).Returns("valid_token");

        var handler = new VerifyOTPCommandHandler(_context, _hasherMock.Object, _jwtMock.Object, _sessionCacheMock.Object, _loggerMock.Object);
        var command = new VerifyOTPCommand(mobile, code);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Token.Should().Be("valid_token");
        
        var verification = await _context.OTPVerifications.FirstAsync();
        verification.IsUsed.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WithInvalidOtp_ShouldReturnNull()
    {
        // Arrange
        var mobile = "9876543210";
        var code = "wrong_code";
        
        _context.OTPVerifications.Add(new OTPVerification
        {
            Identifier = mobile,
            CodeHash = "hashed_code",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            IsUsed = false
        });
        await _context.SaveChangesAsync();

        _hasherMock.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var handler = new VerifyOTPCommandHandler(_context, _hasherMock.Object, _jwtMock.Object, _sessionCacheMock.Object, _loggerMock.Object);
        var command = new VerifyOTPCommand(mobile, code);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Token.Should().BeNull();
    }
}
