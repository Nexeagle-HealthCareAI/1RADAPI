using _1Rad.Application.Features.Auth.Commands.SwitchContext;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace _1Rad.UnitTests.Features.Auth;

public class SwitchContextCommandHandlerTests
{
    private readonly Mock<IJwtProvider> _jwtMock;
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<IUserContext> _userContextMock;
    private readonly Mock<ILogger<SwitchContextCommandHandler>> _loggerMock;
    private readonly ApplicationDbContext _context;

    public SwitchContextCommandHandlerTests()
    {
        _jwtMock = new Mock<IJwtProvider>();
        _publisherMock = new Mock<IPublisher>();
        _userContextMock = new Mock<IUserContext>();
        _loggerMock = new Mock<ILogger<SwitchContextCommandHandler>>();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options, _publisherMock.Object, _userContextMock.Object);
    }

    [Fact]
    public async Task Handle_WithAuthorizedHospital_ShouldReturnNewToken()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var targetHospitalId = Guid.NewGuid();
        
        _userContextMock.Setup(x => x.UserId).Returns(userId);

        var user = new User { UserId = userId, FullName = "Bruce Wayne" };
        var mapping = new UserHospitalMapping
        {
            UserId = userId,
            HospitalId = targetHospitalId,
            User = user,
            Hospital = new Hospital { HospitalId = targetHospitalId, HospitalName = "Arkham Asylum" }
        };
        mapping.Roles.Add(new Role { RoleId = 1, RoleName = "Administrator" });

        _context.Users.Add(user);
        _context.UserHospitalMappings.Add(mapping);
        await _context.SaveChangesAsync();

        _jwtMock.Setup(x => x.GenerateContextualToken(It.IsAny<User>(), It.IsAny<UserHospitalMapping>(), It.IsAny<IEnumerable<Guid>>()))
                .Returns("new_switched_token");

        var handler = new SwitchContextCommandHandler(_context, _userContextMock.Object, _jwtMock.Object, _loggerMock.Object);
        var command = new SwitchContextCommand(targetHospitalId);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.AccessToken.Should().Be("new_switched_token");
    }

    [Fact]
    public async Task Handle_WithUnauthorizedHospital_ShouldReturnFailure()
    {
        // Arrange
        var userId = Guid.NewGuid();
        _userContextMock.Setup(x => x.UserId).Returns(userId);

        var handler = new SwitchContextCommandHandler(_context, _userContextMock.Object, _jwtMock.Object, _loggerMock.Object);
        var command = new SwitchContextCommand(Guid.NewGuid()); // Random ID user has no mapping for

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("You do not have access to the selected hospital.");
    }
}
