using _1Rad.Application.Features.Auth.Commands.ResetPassword;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Enums;
using _1Rad.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using System.Security.Claims;

namespace _1Rad.UnitTests.Features.Auth;

public class ResetPasswordCommandHandlerTests
{
    private readonly Mock<IPasswordHasher> _hasherMock;
    private readonly Mock<IJwtProvider> _jwtProviderMock;
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<IUserContext> _userContextMock;
    private readonly ApplicationDbContext _context;

    public ResetPasswordCommandHandlerTests()
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
    public async Task Handle_WithValidToken_ShouldResetPasswordAndInvalidateSessions()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            UserId = userId,
            Email = "user@example.com",
            Mobile = "+1234567890",
            FullName = "Test User",
            PasswordHash = "old_hashed_password",
            Status = UserStatus.Active
        };
        _context.Users.Add(user);

        var refreshToken1 = new RefreshToken
        {
            UserId = userId,
            Token = "refresh_token_1",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        var refreshToken2 = new RefreshToken
        {
            UserId = userId,
            Token = "refresh_token_2",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        _context.RefreshTokens.AddRange(refreshToken1, refreshToken2);
        await _context.SaveChangesAsync();

        var claims = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("type", "password-reset")
        }));

        _jwtProviderMock.Setup(x => x.ValidateToken("valid_reset_token", "password-reset"))
            .Returns(claims);
        _hasherMock.Setup(x => x.Hash("NewPassword123!")).Returns("new_hashed_password");

        var handler = new ResetPasswordCommandHandler(_context, _hasherMock.Object, _jwtProviderMock.Object);
        var command = new ResetPasswordCommand("valid_reset_token", "NewPassword123!");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();

        var updatedUser = await _context.Users.FindAsync(userId);
        updatedUser!.PasswordHash.Should().Be("new_hashed_password");

        var remainingTokens = await _context.RefreshTokens.Where(t => t.UserId == userId).ToListAsync();
        remainingTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WithInvalidToken_ShouldReturnError()
    {
        // Arrange
        _jwtProviderMock.Setup(x => x.ValidateToken("invalid_token", "password-reset"))
            .Returns((ClaimsPrincipal?)null);

        var handler = new ResetPasswordCommandHandler(_context, _hasherMock.Object, _jwtProviderMock.Object);
        var command = new ResetPasswordCommand("invalid_token", "NewPassword123!");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid or expired reset token.");
    }

    [Fact]
    public async Task Handle_WithExpiredToken_ShouldReturnError()
    {
        // Arrange
        _jwtProviderMock.Setup(x => x.ValidateToken("expired_token", "password-reset"))
            .Returns((ClaimsPrincipal?)null);

        var handler = new ResetPasswordCommandHandler(_context, _hasherMock.Object, _jwtProviderMock.Object);
        var command = new ResetPasswordCommand("expired_token", "NewPassword123!");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Invalid or expired");
    }

    [Fact]
    public async Task Handle_WithNonExistentUser_ShouldReturnError()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var claims = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("type", "password-reset")
        }));

        _jwtProviderMock.Setup(x => x.ValidateToken("valid_token", "password-reset"))
            .Returns(claims);

        var handler = new ResetPasswordCommandHandler(_context, _hasherMock.Object, _jwtProviderMock.Object);
        var command = new ResetPasswordCommand("valid_token", "NewPassword123!");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Account not found.");
    }

    [Fact]
    public async Task Handle_WithMissingSubClaim_ShouldReturnError()
    {
        // Arrange
        var claims = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("type", "password-reset")
            // Missing "sub" claim
        }));

        _jwtProviderMock.Setup(x => x.ValidateToken("token_without_sub", "password-reset"))
            .Returns(claims);

        var handler = new ResetPasswordCommandHandler(_context, _hasherMock.Object, _jwtProviderMock.Object);
        var command = new ResetPasswordCommand("token_without_sub", "NewPassword123!");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid token claims.");
    }
}
