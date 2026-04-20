using _1Rad.Application.Features.Personnel.Commands.RemoveStaff;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Enums;
using _1Rad.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace _1Rad.UnitTests.Features.Personnel;

public class RemoveStaffCommandHandlerTests
{
    private readonly Mock<IUserContext> _userContextMock;
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<ILogger<RemoveStaffCommandHandler>> _loggerMock;
    private readonly ApplicationDbContext _context;

    public RemoveStaffCommandHandlerTests()
    {
        _userContextMock = new Mock<IUserContext>();
        _publisherMock = new Mock<IPublisher>();
        _loggerMock = new Mock<ILogger<RemoveStaffCommandHandler>>();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options, _publisherMock.Object, _userContextMock.Object);
    }

    [Fact]
    public async Task Handle_WithValidUserId_ShouldRemoveMapping()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var hospitalId = Guid.NewGuid();

        var user = new User
        {
            UserId = userId,
            FullName = "Dr. John Doe",
            Email = "john.doe@hospital.com",
            Mobile = "+1234567890",
            PasswordHash = "hashed_password",
            Status = UserStatus.Active
        };

        var hospital = new Hospital { HospitalId = hospitalId, HospitalName = "Test Hospital" };
        var role = new Role { RoleId = 1, RoleName = "Doctor" };

        var mapping = new UserHospitalMapping
        {
            UserId = userId,
            HospitalId = hospitalId,
            IsDefault = true
        };
        mapping.Roles.Add(role);
        user.HospitalMappings.Add(mapping);

        _context.Users.Add(user);
        _context.Hospitals.Add(hospital);
        _context.Roles.Add(role);
        await _context.SaveChangesAsync();

        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId);

        var handler = new RemoveStaffCommandHandler(_context);
        var command = new RemoveStaffCommand(userId, hospitalId);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();

        var updatedUser = await _context.Users
            .Include(u => u.HospitalMappings)
            .FirstOrDefaultAsync(u => u.UserId == userId);

        updatedUser!.HospitalMappings.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WithUserHavingMultipleMappings_ShouldOnlyRemoveCurrentHospitalMapping()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var hospitalId1 = Guid.NewGuid();
        var hospitalId2 = Guid.NewGuid();

        var user = new User
        {
            UserId = userId,
            FullName = "Dr. John Doe",
            Email = "john.doe@hospital.com",
            Mobile = "+1234567890",
            PasswordHash = "hashed_password",
            Status = UserStatus.Active
        };

        var hospital1 = new Hospital { HospitalId = hospitalId1, HospitalName = "Hospital 1" };
        var hospital2 = new Hospital { HospitalId = hospitalId2, HospitalName = "Hospital 2" };
        var role = new Role { RoleId = 1, RoleName = "Doctor" };

        var mapping1 = new UserHospitalMapping
        {
            UserId = userId,
            HospitalId = hospitalId1,
            IsDefault = true
        };
        mapping1.Roles.Add(role);

        var mapping2 = new UserHospitalMapping
        {
            UserId = userId,
            HospitalId = hospitalId2,
            IsDefault = false
        };
        mapping2.Roles.Add(role);

        user.HospitalMappings.Add(mapping1);
        user.HospitalMappings.Add(mapping2);

        _context.Users.Add(user);
        _context.Hospitals.AddRange(hospital1, hospital2);
        _context.Roles.Add(role);
        await _context.SaveChangesAsync();

        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId1);

        var handler = new RemoveStaffCommandHandler(_context);
        var command = new RemoveStaffCommand(userId, hospitalId1);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();

        var updatedUser = await _context.Users
            .Include(u => u.HospitalMappings)
            .FirstOrDefaultAsync(u => u.UserId == userId);

        updatedUser!.HospitalMappings.Should().HaveCount(1);
        updatedUser.HospitalMappings.First().HospitalId.Should().Be(hospitalId2);
    }

    [Fact]
    public async Task Handle_WithNonExistentUser_ShouldReturnError()
    {
        // Arrange
        var hospitalId = Guid.NewGuid();
        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId);

        var handler = new RemoveStaffCommandHandler(_context);
        var command = new RemoveStaffCommand(Guid.NewGuid(), hospitalId);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Handle_WithUserFromDifferentHospital_ShouldReturnError()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var hospitalId1 = Guid.NewGuid();
        var hospitalId2 = Guid.NewGuid();

        var user = new User
        {
            UserId = userId,
            FullName = "Dr. John Doe",
            Email = "john.doe@hospital.com",
            Mobile = "+1234567890",
            PasswordHash = "hashed_password",
            Status = UserStatus.Active
        };

        var hospital1 = new Hospital { HospitalId = hospitalId1, HospitalName = "Hospital 1" };
        var hospital2 = new Hospital { HospitalId = hospitalId2, HospitalName = "Hospital 2" };
        var role = new Role { RoleId = 1, RoleName = "Doctor" };

        var mapping = new UserHospitalMapping
        {
            UserId = userId,
            HospitalId = hospitalId1,
            IsDefault = true
        };
        mapping.Roles.Add(role);
        user.HospitalMappings.Add(mapping);

        _context.Users.Add(user);
        _context.Hospitals.AddRange(hospital1, hospital2);
        _context.Roles.Add(role);
        await _context.SaveChangesAsync();

        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId2);

        var handler = new RemoveStaffCommandHandler(_context);
        var command = new RemoveStaffCommand(userId, hospitalId2);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }
}
