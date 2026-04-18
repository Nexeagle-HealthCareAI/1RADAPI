using _1Rad.Application.Features.Auth.Commands.IdentitySetup;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace _1Rad.UnitTests.Features.Auth;

public class IdentitySetupCommandHandlerTests
{
    private readonly Mock<IPasswordHasher> _hasherMock;
    private readonly Mock<IJwtProvider> _jwtMock;
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<ILogger<IdentitySetupCommandHandler>> _loggerMock;
    private readonly ApplicationDbContext _context;

    public IdentitySetupCommandHandlerTests()
    {
        _hasherMock = new Mock<IPasswordHasher>();
        _jwtMock = new Mock<IJwtProvider>();
        _publisherMock = new Mock<IPublisher>();
        _loggerMock = new Mock<ILogger<IdentitySetupCommandHandler>>();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options, _publisherMock.Object);
    }

    [Fact]
    public async Task Handle_WithNewUser_ShouldCreateUserAndReturnToken()
    {
        // Arrange
        var command = new IdentitySetupCommand("John Doe", "john@example.com", "9876543210", "Password123!");
        
        _hasherMock.Setup(x => x.Hash(It.IsAny<string>())).Returns("hashed_password");
        _jwtMock.Setup(x => x.GenerateInitiationToken(It.IsAny<string>(), It.IsAny<Guid>())).Returns("updated_token");

        var handler = new IdentitySetupCommandHandler(_context, _hasherMock.Object, _jwtMock.Object, _loggerMock.Object);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.UserId.Should().NotBeNull();
        result.Token.Should().Be("updated_token");
        result.Error.Should().BeNull();

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == "john@example.com");
        user.Should().NotBeNull();
        user!.FullName.Should().Be("John Doe");
    }

    [Fact]
    public async Task Handle_WithDuplicateEmail_ShouldReturnError()
    {
        // Arrange
        _context.Users.Add(new User { Email = "duplicate@example.com", FullName = "Existing", Mobile = "111", PasswordHash = "h" });
        await _context.SaveChangesAsync();

        var command = new IdentitySetupCommand("John Doe", "duplicate@example.com", "9876543210", "Password123!");
        var handler = new IdentitySetupCommandHandler(_context, _hasherMock.Object, _jwtMock.Object, _loggerMock.Object);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.UserId.Should().BeNull();
        result.Error.Should().Be("Email already in use.");
    }
}
