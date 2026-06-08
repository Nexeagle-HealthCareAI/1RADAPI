using _1Rad.Application.Features.Auth.Commands.TokenRefresh;
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

public class RefreshTokenCommandHandlerTests
{
    private readonly Mock<IJwtProvider> _jwtMock;
    private readonly Mock<IActiveSessionCache> _sessionCacheMock;
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<IUserContext> _userContextMock;
    private readonly Mock<ILogger<RefreshTokenCommandHandler>> _loggerMock;
    private readonly ApplicationDbContext _context;

    public RefreshTokenCommandHandlerTests()
    {
        _jwtMock = new Mock<IJwtProvider>();
        _sessionCacheMock = new Mock<IActiveSessionCache>();
        _publisherMock = new Mock<IPublisher>();
        _userContextMock = new Mock<IUserContext>();
        _loggerMock = new Mock<ILogger<RefreshTokenCommandHandler>>();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options, _publisherMock.Object, _userContextMock.Object);
    }

    [Fact]
    public async Task Handle_WithValidRefreshToken_ShouldReturnNewTokens()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var oldTokenString = "old_refresh_token";
        var newTokenString = "new_refresh_token";

        var user = new User
        {
            UserId = userId,
            FullName = "Hal Jordan",
            Status = UserStatus.Active
        };

        var hospitalId = Guid.NewGuid();
        var mapping = new UserHospitalMapping
        {
            UserId = userId,
            HospitalId = hospitalId,
            IsDefault = true,
            Hospital = new Hospital { HospitalId = hospitalId, HospitalName = "Oa" },
            Roles = new List<Role> { new Role { RoleName = "Green Lantern" } }
        };

        user.HospitalMappings.Add(mapping);
        _context.Users.Add(user);

        var oldTokenEntity = new RefreshToken
        {
            UserId = userId,
            Token = oldTokenString,
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        };
        _context.RefreshTokens.Add(oldTokenEntity);
        await _context.SaveChangesAsync();

        _jwtMock.Setup(x => x.GenerateRefreshToken()).Returns(newTokenString);
        _jwtMock.Setup(x => x.GenerateContextualToken(It.IsAny<User>(), It.IsAny<UserHospitalMapping>(), It.IsAny<IEnumerable<Guid>>(), It.IsAny<Guid?>()))
                .Returns("new_access_token");

        var handler = new RefreshTokenCommandHandler(_context, _jwtMock.Object, _sessionCacheMock.Object, _loggerMock.Object);
        var command = new RefreshTokenCommand(oldTokenString);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.AccessToken.Should().Be("new_access_token");
        result.RefreshToken.Should().Be(newTokenString);

        var oldTokenResult = await _context.RefreshTokens.FirstOrDefaultAsync(t => t.Token == oldTokenString);
        oldTokenResult!.RevokedAt.Should().NotBeNull();
        oldTokenResult!.ReplacedByToken.Should().Be(newTokenString);

        var newTokenResult = await _context.RefreshTokens.FirstOrDefaultAsync(t => t.Token == newTokenString);
        newTokenResult.Should().NotBeNull();
        newTokenResult!.UserId.Should().Be(userId);
    }

    [Fact]
    public async Task Handle_WithInvalidToken_ShouldReturnFailure()
    {
        // Act
        var handler = new RefreshTokenCommandHandler(_context, _jwtMock.Object, _sessionCacheMock.Object, _loggerMock.Object);
        var command = new RefreshTokenCommand("invalid_token");
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid or expired refresh token.");
    }
}
