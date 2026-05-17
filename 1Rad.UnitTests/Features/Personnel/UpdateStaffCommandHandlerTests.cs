using _1Rad.Application.Features.Personnel.Commands.UpdateStaff;
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

public class UpdateStaffCommandHandlerTests
{
    private readonly Mock<IUserContext> _userContextMock;
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<ILogger<UpdateStaffCommandHandler>> _loggerMock;
    private readonly Mock<IPasswordHasher> _passwordHasherMock;
    private readonly ApplicationDbContext _context;

    public UpdateStaffCommandHandlerTests()
    {
        _userContextMock = new Mock<IUserContext>();
        _publisherMock = new Mock<IPublisher>();
        _loggerMock = new Mock<ILogger<UpdateStaffCommandHandler>>();
        _passwordHasherMock = new Mock<IPasswordHasher>();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options, _publisherMock.Object, _userContextMock.Object);
    }

    [Fact]
    public async Task Handle_WithValidData_ShouldUpdateStaffDetails()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var hospitalId = Guid.NewGuid();
        var oldRoleId = 1;
        var newRoleId = 2;

        var user = new User
        {
            UserId = userId,
            FullName = "Dr. John Doe",
            Email = "john.doe@hospital.com",
            Mobile = "+1234567890",
            PasswordHash = "hashed_password",
            Status = UserStatus.Active,
            Specialization = "Cardiology",
            Degree = "MD",
            LicenseNo = "LIC123"
        };

        var hospital = new Hospital { HospitalId = hospitalId, HospitalName = "Test Hospital" };
        var oldRole = new Role { RoleId = oldRoleId, RoleName = "doctor" };
        var newRole = new Role { RoleId = newRoleId, RoleName = "senior doctor" };

        var mapping = new UserHospitalMapping
        {
            UserId = userId,
            HospitalId = hospitalId,
            IsDefault = true
        };
        mapping.Roles.Add(oldRole);
        user.HospitalMappings.Add(mapping);

        _context.Users.Add(user);
        _context.Hospitals.Add(hospital);
        _context.Roles.AddRange(oldRole, newRole);
        await _context.SaveChangesAsync();

        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId);

        var handler = new UpdateStaffCommandHandler(_context, _passwordHasherMock.Object);
        var command = new UpdateStaffCommand(
            userId,
            hospitalId,
            "Dr. John Smith",
            new List<string> { "senior doctor" },
            "Neurology",
            "MD, PhD",
            "LIC456");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();

        var updatedUser = await _context.Users
            .Include(u => u.HospitalMappings)
            .ThenInclude(m => m.Roles)
            .FirstOrDefaultAsync(u => u.UserId == userId);

        updatedUser!.FullName.Should().Be("Dr. John Smith");
        updatedUser.Specialization.Should().Be("Neurology");
        updatedUser.Degree.Should().Be("MD, PhD");
        updatedUser.LicenseNo.Should().Be("LIC456");

        var updatedMapping = updatedUser.HospitalMappings.First(m => m.HospitalId == hospitalId);
        updatedMapping.Roles.Should().HaveCount(1);
        updatedMapping.Roles.First().RoleId.Should().Be(newRoleId);
    }

    [Fact]
    public async Task Handle_WithNonExistentUser_ShouldReturnError()
    {
        // Arrange
        var hospitalId = Guid.NewGuid();
        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId);

        var handler = new UpdateStaffCommandHandler(_context, _passwordHasherMock.Object);
        var command = new UpdateStaffCommand(
            Guid.NewGuid(),
            hospitalId,
            "Dr. John Smith",
            new List<string> { "doctor" });

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
        var role = new Role { RoleId = 1, RoleName = "doctor" };

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

        var handler = new UpdateStaffCommandHandler(_context, _passwordHasherMock.Object);
        var command = new UpdateStaffCommand(
            userId,
            hospitalId2,
            "Dr. John Smith",
            new List<string> { "doctor" });

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Handle_WithMultipleRoles_ShouldUpdateAllRoles()
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
        var role1 = new Role { RoleId = 1, RoleName = "doctor" };
        var role2 = new Role { RoleId = 2, RoleName = "surgeon" };
        var role3 = new Role { RoleId = 3, RoleName = "consultant" };

        var mapping = new UserHospitalMapping
        {
            UserId = userId,
            HospitalId = hospitalId,
            IsDefault = true
        };
        mapping.Roles.Add(role1);
        user.HospitalMappings.Add(mapping);

        _context.Users.Add(user);
        _context.Hospitals.Add(hospital);
        _context.Roles.AddRange(role1, role2, role3);
        await _context.SaveChangesAsync();

        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId);

        var handler = new UpdateStaffCommandHandler(_context, _passwordHasherMock.Object);
        var command = new UpdateStaffCommand(
            userId,
            hospitalId,
            "Dr. John Doe",
            new List<string> { "surgeon", "consultant" });

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();

        var updatedUser = await _context.Users
            .Include(u => u.HospitalMappings)
            .ThenInclude(m => m.Roles)
            .FirstOrDefaultAsync(u => u.UserId == userId);

        var updatedMapping = updatedUser!.HospitalMappings.First(m => m.HospitalId == hospitalId);
        updatedMapping.Roles.Should().HaveCount(2);
        updatedMapping.Roles.Select(r => r.RoleId).Should().Contain(new[] { 2, 3 });
    }
}
