using _1Rad.Application.Features.Auth.Commands.SendOTP;
using _1Rad.Application.Interfaces;
using _1Rad.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace _1Rad.UnitTests.Features.Auth;

public class SendOTPCommandHandlerTests
{
    private readonly Mock<ISmsService> _smsMock;
    private readonly Mock<IEmailService> _emailMock;
    private readonly Mock<IPasswordHasher> _hasherMock;
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<IUserContext> _userContextMock;
    private readonly Mock<ILogger<SendOTPCommandHandler>> _loggerMock;
    private readonly ApplicationDbContext _context;

    public SendOTPCommandHandlerTests()
    {
        _smsMock = new Mock<ISmsService>();
        _emailMock = new Mock<IEmailService>();
        _hasherMock = new Mock<IPasswordHasher>();
        _publisherMock = new Mock<IPublisher>();
        _userContextMock = new Mock<IUserContext>();
        _loggerMock = new Mock<ILogger<SendOTPCommandHandler>>();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options, _publisherMock.Object, _userContextMock.Object);
    }

    [Fact]
    public async Task Handle_ShouldGenerateOtpAndSaveToDb()
    {
        // Arrange
        var command = new SendOTPCommand("9876543210");
        _hasherMock.Setup(x => x.Hash(It.IsAny<string>())).Returns("hashed_otp");
        
        var handler = new SendOTPCommandHandler(_context, _smsMock.Object, _emailMock.Object, _hasherMock.Object, _loggerMock.Object);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        
        var verification = await _context.OTPVerifications.FirstOrDefaultAsync(x => x.Identifier == "9876543210");
        verification.Should().NotBeNull();
        verification!.CodeHash.Should().Be("hashed_otp");
        
        _smsMock.Verify(x => x.SendOtpAsync("9876543210", It.IsAny<string>()), Times.Once);
    }
}
