using _1Rad.Application.Features.Auth.Commands.DeployInfrastructure;
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

public class DeployInfrastructureCommandHandlerTests
{
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<IUserContext> _userContextMock;
    private readonly Mock<ILogger<DeployInfrastructureCommandHandler>> _loggerMock;
    private readonly ApplicationDbContext _context;

    public DeployInfrastructureCommandHandlerTests()
    {
        _publisherMock = new Mock<IPublisher>();
        _userContextMock = new Mock<IUserContext>();
        _loggerMock = new Mock<ILogger<DeployInfrastructureCommandHandler>>();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options, _publisherMock.Object, _userContextMock.Object);
    }

    [Fact]
    public async Task Handle_WithValidRequest_ShouldCreateInfrastructureAndPromoteUser()
    {
        // Arrange
        var user = new User { UserId = Guid.NewGuid(), FullName = "Admin", Email = "admin@1rad.com", Mobile = "111", PasswordHash = "h", Status = UserStatus.Pending };
        _context.Users.Add(user);
        _context.Roles.Add(new Role { RoleId = 1, RoleName = "AdminDoctor" });
        await _context.SaveChangesAsync();

        var command = new DeployInfrastructureCommand(user.UserId, "Chain A", "Hospital 1", "Address 1", "AdminDoctor", null, null, null, null, "Radiology", "MBBS, MD", "LIC123");
        var handler = new DeployInfrastructureCommandHandler(_context, _loggerMock.Object);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        
        var hospital = await _context.Hospitals.FirstAsync();
        hospital.HospitalName.Should().Be("Hospital 1");
        
        var group = await _context.HospitalGroups.FirstAsync();
        group.GroupName.Should().Be("Chain A");

        var updatedUser = await _context.Users.FindAsync(user.UserId);
        updatedUser!.Status.Should().Be(UserStatus.Active);
        updatedUser.IsVerified.Should().BeTrue();
        updatedUser.Specialization.Should().Be("Radiology");
        updatedUser.Degree.Should().Be("MBBS, MD");
        updatedUser.LicenseNo.Should().Be("LIC123");

        var mapping = await _context.UserHospitalMappings.FirstOrDefaultAsync(m => m.UserId == user.UserId && m.HospitalId == hospital.HospitalId);
        mapping.Should().NotBeNull();
    }
}
