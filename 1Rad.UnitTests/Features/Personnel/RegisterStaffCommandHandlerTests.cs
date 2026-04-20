using _1Rad.Application.Features.Personnel.Commands.RegisterStaff;
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

public class RegisterStaffCommandHandlerTests
{
    private readonly Mock<IPasswordHasher> _hasherMock;
    private readonly Mock<IUserContext> _userContextMock;
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<ILogger<RegisterStaffCommandHandler>> _loggerMock;
    private readonly ApplicationDbContext _context;

    public RegisterStaffCommandHandlerTests()
    {
        _hasherMock = new Mock<IPasswordHasher>();
        _userContextMock = new Mock<IUserContext>();
        _publisherMock = new Mock<IPublisher>();
        _loggerMock = new Mock<ILogger<RegisterStaffCommandHandler>>();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options, _publisherMock.Object, _userContextMock.Object);
    }

    [Fact]
    public async Task Handle_WithNewUser_ShouldCreateUserAndMapping()
    {
        // Arrange
        var hospitalId = Guid.NewGuid();
        var roleId = 1;

        var hospital = new Hospital
        {
            HospitalId = hospitalId,
            HospitalName = "Test Hospital",
            HospitalAddress = "123 Test St"
        };
        var role = new Role
        {
            RoleId = roleId,
            RoleName = "doctor"
        };
        _context.Hospitals.Add(hospital);
        _context.Roles.Add(role);
        await _context.SaveChangesAsync();

        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId);
        _hasherMock.Setup(x => x.Hash("Password123!")).Returns("hashed_password");

        var handler = new RegisterStaffCommandHandler(_context, _hasherMock.Object);
        var command = new RegisterStaffCommand(
            hospitalId,
            "Dr. John Doe",
            "john.doe@hospital.com",
            "+1234567890",
            "Password123!",
            new List<string> { "doctor" },
            "Cardiology",
            "MD",
            "LIC123456");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.UserId.Should().NotBeEmpty();
        result.Error.Should().BeNull();

        var user = await _context.Users.Include(u => u.HospitalMappings)
            .ThenInclude(m => m.Roles)
            .FirstOrDefaultAsync(u => u.UserId == result.UserId);

        user.Should().NotBeNull();
        user!.FullName.Should().Be("Dr. John Doe");
        user.Email.Should().Be("john.doe@hospital.com");
        user.Mobile.Should().Be("+1234567890");
        user.Status.Should().Be(UserStatus.Active);
        user.IsVerified.Should().BeTrue();
        user.Specialization.Should().Be("Cardiology");
        user.Degree.Should().Be("MD");
        user.LicenseNo.Should().Be("LIC123456");

        user.HospitalMappings.Should().HaveCount(1);
        user.HospitalMappings.First().HospitalId.Should().Be(hospitalId);
        user.HospitalMappings.First().Roles.Should().HaveCount(1);
        user.HospitalMappings.First().Roles.First().RoleId.Should().Be(roleId);
    }

    [Fact]
    public async Task Handle_WithExistingUser_ShouldAddMappingToExistingUser()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var hospitalId1 = Guid.NewGuid();
        var hospitalId2 = Guid.NewGuid();
        var roleId = 1;

        var existingUser = new User
        {
            UserId = userId,
            FullName = "Dr. John Doe",
            Email = "john.doe@hospital.com",
            Mobile = "+1234567890",
            PasswordHash = "existing_hash",
            Status = UserStatus.Active,
            IsVerified = true
        };

        var hospital1 = new Hospital { HospitalId = hospitalId1, HospitalName = "Hospital 1" };
        var hospital2 = new Hospital { HospitalId = hospitalId2, HospitalName = "Hospital 2" };
        var role = new Role { RoleId = roleId, RoleName = "doctor" };

        var existingMapping = new UserHospitalMapping
        {
            UserId = userId,
            HospitalId = hospitalId1,
            IsDefault = true
        };
        existingMapping.Roles.Add(role);

        existingUser.HospitalMappings.Add(existingMapping);

        _context.Users.Add(existingUser);
        _context.Hospitals.AddRange(hospital1, hospital2);
        _context.Roles.Add(role);
        await _context.SaveChangesAsync();

        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId2);

        var handler = new RegisterStaffCommandHandler(_context, _hasherMock.Object);
        var command = new RegisterStaffCommand(
            hospitalId2,
            "Dr. John Doe",
            "john.doe@hospital.com",
            "+1234567890",
            "Password123!",
            new List<string> { "doctor" });

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.UserId.Should().Be(userId);
        result.Error.Should().BeNull();

        var user = await _context.Users.Include(u => u.HospitalMappings)
            .ThenInclude(m => m.Roles)
            .FirstOrDefaultAsync(u => u.UserId == userId);

        user!.HospitalMappings.Should().HaveCount(2);
        user.HospitalMappings.Should().Contain(m => m.HospitalId == hospitalId2);
    }

    [Fact]
    public async Task Handle_WithDuplicateMapping_ShouldReturnError()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var hospitalId = Guid.NewGuid();
        var roleId = 1;

        var existingUser = new User
        {
            UserId = userId,
            FullName = "Dr. John Doe",
            Email = "john.doe@hospital.com",
            Mobile = "+1234567890",
            PasswordHash = "existing_hash",
            Status = UserStatus.Active,
            IsVerified = true
        };

        var hospital = new Hospital { HospitalId = hospitalId, HospitalName = "Hospital 1" };
        var role = new Role { RoleId = roleId, RoleName = "doctor" };

        var existingMapping = new UserHospitalMapping
        {
            UserId = userId,
            HospitalId = hospitalId,
            IsDefault = true
        };
        existingMapping.Roles.Add(role);

        existingUser.HospitalMappings.Add(existingMapping);

        _context.Users.Add(existingUser);
        _context.Hospitals.Add(hospital);
        _context.Roles.Add(role);
        await _context.SaveChangesAsync();

        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId);

        var handler = new RegisterStaffCommandHandler(_context, _hasherMock.Object);
        var command = new RegisterStaffCommand(
            hospitalId,
            "Dr. John Doe",
            "john.doe@hospital.com",
            "+1234567890",
            "Password123!",
            new List<string> { "doctor" });

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.UserId.Should().Be(userId);
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WithInvalidRole_ShouldThrowException()
    {
        // Arrange
        var hospitalId = Guid.NewGuid();
        var hospital = new Hospital { HospitalId = hospitalId, HospitalName = "Test Hospital" };
        _context.Hospitals.Add(hospital);
        await _context.SaveChangesAsync();

        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId);
        _hasherMock.Setup(x => x.Hash(It.IsAny<string>())).Returns("hashed_password");

        var handler = new RegisterStaffCommandHandler(_context, _hasherMock.Object);
        var command = new RegisterStaffCommand(
            hospitalId,
            "Dr. John Doe",
            "john.doe@hospital.com",
            "+1234567890",
            "Password123!",
            new List<string> { "NonExistentRole" });

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.UserId.Should().BeEmpty();
        result.Error.Should().Contain("Invalid roles");
    }
}
