using _1Rad.Application.Features.Personnel.Queries.GetHospitalPersonnel;
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

public class GetHospitalPersonnelQueryHandlerTests
{
    private readonly Mock<IUserContext> _userContextMock;
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<ILogger<GetHospitalPersonnelQueryHandler>> _loggerMock;
    private readonly ApplicationDbContext _context;

    public GetHospitalPersonnelQueryHandlerTests()
    {
        _userContextMock = new Mock<IUserContext>();
        _publisherMock = new Mock<IPublisher>();
        _loggerMock = new Mock<ILogger<GetHospitalPersonnelQueryHandler>>();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options, _publisherMock.Object, _userContextMock.Object);
    }

    [Fact]
    public async Task Handle_ShouldReturnAllPersonnelForCurrentHospital()
    {
        // Arrange
        var hospitalId = Guid.NewGuid();
        var hospital = new Hospital { HospitalId = hospitalId, HospitalName = "Test Hospital" };

        var user1 = new User
        {
            UserId = Guid.NewGuid(),
            FullName = "Dr. John Doe",
            Email = "john@hospital.com",
            Mobile = "+1234567890",
            PasswordHash = "hash",
            Status = UserStatus.Active,
            Specialization = "Cardiology",
            Degree = "MD"
        };

        var user2 = new User
        {
            UserId = Guid.NewGuid(),
            FullName = "Dr. Jane Smith",
            Email = "jane@hospital.com",
            Mobile = "+0987654321",
            PasswordHash = "hash",
            Status = UserStatus.Active,
            Specialization = "Neurology",
            Degree = "MD, PhD"
        };

        var role1 = new Role { RoleId = 1, RoleName = "Doctor" };
        var role2 = new Role { RoleId = 2, RoleName = "Surgeon" };

        var mapping1 = new UserHospitalMapping
        {
            UserId = user1.UserId,
            HospitalId = hospitalId,
            IsDefault = true
        };
        mapping1.Roles.Add(role1);

        var mapping2 = new UserHospitalMapping
        {
            UserId = user2.UserId,
            HospitalId = hospitalId,
            IsDefault = true
        };
        mapping2.Roles.Add(role2);

        user1.HospitalMappings.Add(mapping1);
        user2.HospitalMappings.Add(mapping2);

        _context.Hospitals.Add(hospital);
        _context.Users.AddRange(user1, user2);
        _context.Roles.AddRange(role1, role2);
        await _context.SaveChangesAsync();

        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId);

        var handler = new GetHospitalPersonnelQueryHandler(_context, _loggerMock.Object);
        var query = new GetHospitalPersonnelQuery(hospitalId);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(p => p.FullName == "Dr. John Doe");
        result.Should().Contain(p => p.FullName == "Dr. Jane Smith");
        result.First(p => p.FullName == "Dr. John Doe").Roles.Should().Contain("Doctor");
        result.First(p => p.FullName == "Dr. Jane Smith").Roles.Should().Contain("Surgeon");
    }

    [Fact]
    public async Task Handle_ShouldNotReturnPersonnelFromOtherHospitals()
    {
        // Arrange
        var hospitalId1 = Guid.NewGuid();
        var hospitalId2 = Guid.NewGuid();

        var hospital1 = new Hospital { HospitalId = hospitalId1, HospitalName = "Hospital 1" };
        var hospital2 = new Hospital { HospitalId = hospitalId2, HospitalName = "Hospital 2" };

        var user1 = new User
        {
            UserId = Guid.NewGuid(),
            FullName = "Dr. John Doe",
            Email = "john@hospital1.com",
            Mobile = "+1234567890",
            PasswordHash = "hash",
            Status = UserStatus.Active
        };

        var user2 = new User
        {
            UserId = Guid.NewGuid(),
            FullName = "Dr. Jane Smith",
            Email = "jane@hospital2.com",
            Mobile = "+0987654321",
            PasswordHash = "hash",
            Status = UserStatus.Active
        };

        var role = new Role { RoleId = 1, RoleName = "Doctor" };

        var mapping1 = new UserHospitalMapping
        {
            UserId = user1.UserId,
            HospitalId = hospitalId1,
            IsDefault = true
        };
        mapping1.Roles.Add(role);

        var mapping2 = new UserHospitalMapping
        {
            UserId = user2.UserId,
            HospitalId = hospitalId2,
            IsDefault = true
        };
        mapping2.Roles.Add(role);

        user1.HospitalMappings.Add(mapping1);
        user2.HospitalMappings.Add(mapping2);

        _context.Hospitals.AddRange(hospital1, hospital2);
        _context.Users.AddRange(user1, user2);
        _context.Roles.Add(role);
        await _context.SaveChangesAsync();

        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId1);

        var handler = new GetHospitalPersonnelQueryHandler(_context, _loggerMock.Object);
        var query = new GetHospitalPersonnelQuery(hospitalId1);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        result.First().FullName.Should().Be("Dr. John Doe");
    }

    [Fact]
    public async Task Handle_WithNoPersonnel_ShouldReturnEmptyList()
    {
        // Arrange
        var hospitalId = Guid.NewGuid();
        var hospital = new Hospital { HospitalId = hospitalId, HospitalName = "Empty Hospital" };
        _context.Hospitals.Add(hospital);
        await _context.SaveChangesAsync();

        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId);

        var handler = new GetHospitalPersonnelQueryHandler(_context, _loggerMock.Object);
        var query = new GetHospitalPersonnelQuery(hospitalId);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ShouldIncludeAllUserDetails()
    {
        // Arrange
        var hospitalId = Guid.NewGuid();
        var hospital = new Hospital { HospitalId = hospitalId, HospitalName = "Test Hospital" };

        var user = new User
        {
            UserId = Guid.NewGuid(),
            FullName = "Dr. John Doe",
            Email = "john@hospital.com",
            Mobile = "+1234567890",
            PasswordHash = "hash",
            Status = UserStatus.Active,
            Specialization = "Cardiology",
            Degree = "MD",
            LicenseNo = "LIC123456"
        };

        var role = new Role { RoleId = 1, RoleName = "Senior Doctor" };

        var mapping = new UserHospitalMapping
        {
            UserId = user.UserId,
            HospitalId = hospitalId,
            IsDefault = true,
            AssignedAt = new DateTime(2024, 1, 1)
        };
        mapping.Roles.Add(role);
        user.HospitalMappings.Add(mapping);

        _context.Hospitals.Add(hospital);
        _context.Users.Add(user);
        _context.Roles.Add(role);
        await _context.SaveChangesAsync();

        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId);

        var handler = new GetHospitalPersonnelQueryHandler(_context, _loggerMock.Object);
        var query = new GetHospitalPersonnelQuery(hospitalId);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        var personnel = result.First();
        personnel.UserId.Should().Be(user.UserId);
        personnel.FullName.Should().Be("Dr. John Doe");
        personnel.Email.Should().Be("john@hospital.com");
        personnel.Mobile.Should().Be("+1234567890");
        personnel.Specialization.Should().Be("Cardiology");
        personnel.Degree.Should().Be("MD");
        personnel.LicenseNo.Should().Be("LIC123456");
        personnel.Roles.Should().Contain("Senior Doctor");
    }
}
