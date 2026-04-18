using _1Rad.Application.Features.Auth.Commands.Login;
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

public class LoginCommandHandlerTests
{
    private readonly Mock<IPasswordHasher> _hasherMock;
    private readonly Mock<IJwtProvider> _jwtMock;
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<IUserContext> _userContextMock;
    private readonly Mock<ILogger<LoginCommandHandler>> _loggerMock;
    private readonly ApplicationDbContext _context;

    public LoginCommandHandlerTests()
    {
        _hasherMock = new Mock<IPasswordHasher>();
        _jwtMock = new Mock<IJwtProvider>();
        _publisherMock = new Mock<IPublisher>();
        _userContextMock = new Mock<IUserContext>();
        _loggerMock = new Mock<ILogger<LoginCommandHandler>>();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options, _publisherMock.Object, _userContextMock.Object);
    }

    [Fact]
    public async Task Handle_WithValidEmail_ShouldReturnSuccessfulResponse()
    {
        // Arrange
        var identifier = "doctor@1rad.com";
        var password = "SecurePassword123";
        var passwordHash = "hashed_password";

        var user = new User
        {
            UserId = Guid.NewGuid(),
            Email = identifier,
            FullName = "Doctor Strange",
            PasswordHash = passwordHash,
            Status = UserStatus.Active
        };

        var hospitalId = Guid.NewGuid();
        var mapping = new UserHospitalMapping
        {
            UserId = user.UserId,
            HospitalId = hospitalId,
            IsDefault = true,
            Hospital = new Hospital { HospitalId = hospitalId, HospitalName = "Sanctum Sanctorum" }
        };
        mapping.Roles.Add(new Role { RoleId = 1, RoleName = "Radiologist" });

        user.HospitalMappings.Add(mapping);
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _hasherMock.Setup(x => x.Verify(password, passwordHash)).Returns(true);
        _jwtMock.Setup(x => x.GenerateContextualToken(It.IsAny<User>(), It.IsAny<UserHospitalMapping>(), It.IsAny<IEnumerable<Guid>>()))
                .Returns("access_token");
        _jwtMock.Setup(x => x.GenerateRefreshToken()).Returns("refresh_token");

        var handler = new LoginCommandHandler(_context, _hasherMock.Object, _jwtMock.Object, _loggerMock.Object);
        var command = new LoginCommand(identifier, password);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.AccessToken.Should().Be("access_token");
        result.RefreshToken.Should().Be("refresh_token");
        result.UserProfile.FullName.Should().Be("Doctor Strange");
        
        var refreshToken = await _context.RefreshTokens.FirstOrDefaultAsync(t => t.UserId == user.UserId);
        refreshToken.Should().NotBeNull();
        refreshToken!.Token.Should().Be("refresh_token");
    }

    [Fact]
    public async Task Handle_WithInvalidPassword_ShouldReturnFailure()
    {
        // Arrange
        var identifier = "doctor@1rad.com";
        var user = new User
        {
            Email = identifier,
            PasswordHash = "correct_hash",
            Status = UserStatus.Active
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _hasherMock.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var handler = new LoginCommandHandler(_context, _hasherMock.Object, _jwtMock.Object, _loggerMock.Object);
        var command = new LoginCommand(identifier, "wrong_password");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid credentials.");
    }
}
