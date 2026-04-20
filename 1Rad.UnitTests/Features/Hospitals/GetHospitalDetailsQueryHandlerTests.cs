using _1Rad.Application.Features.Hospitals.Queries.GetHospitalDetails;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace _1Rad.UnitTests.Features.Hospitals;

public class GetHospitalDetailsQueryHandlerTests
{
    private readonly Mock<IUserContext> _userContextMock;
    private readonly Mock<IPublisher> _publisherMock;
    private readonly ApplicationDbContext _context;

    public GetHospitalDetailsQueryHandlerTests()
    {
        _userContextMock = new Mock<IUserContext>();
        _publisherMock = new Mock<IPublisher>();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options, _publisherMock.Object, _userContextMock.Object);
    }

    [Fact]
    public async Task Handle_WithValidHospital_ShouldReturnHospitalDetails()
    {
        // Arrange
        var hospitalId = Guid.NewGuid();

        var hospital = new Hospital
        {
            HospitalId = hospitalId,
            HospitalName = "Test Hospital",
            HospitalAddress = "123 Test Street, Test City",
            GSTIN = "GSTIN123456",
            RegistrationNumber = "REG123",
            PAN = "PAN123",
            NABHNumber = "NABH123",
            Status = "Active"
        };

        _context.Hospitals.Add(hospital);
        await _context.SaveChangesAsync();

        var handler = new GetHospitalDetailsQueryHandler(_context);
        var query = new GetHospitalDetailsQuery(hospitalId);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.HospitalId.Should().Be(hospitalId);
        result.HospitalName.Should().Be("Test Hospital");
        result.HospitalAddress.Should().Be("123 Test Street, Test City");
        result.GSTIN.Should().Be("GSTIN123456");
        result.RegistrationNumber.Should().Be("REG123");
        result.PAN.Should().Be("PAN123");
        result.NABHNumber.Should().Be("NABH123");
        result.Status.Should().Be("Active");
    }

    [Fact]
    public async Task Handle_WithHospitalWithoutGroup_ShouldReturnDetailsWithNullOptionalFields()
    {
        // Arrange
        var hospitalId = Guid.NewGuid();

        var hospital = new Hospital
        {
            HospitalId = hospitalId,
            HospitalName = "Independent Hospital",
            HospitalAddress = "456 Independent Ave",
            GSTIN = null,
            RegistrationNumber = null,
            PAN = null,
            NABHNumber = null,
            Status = "Active"
        };

        _context.Hospitals.Add(hospital);
        await _context.SaveChangesAsync();

        var handler = new GetHospitalDetailsQueryHandler(_context);
        var query = new GetHospitalDetailsQuery(hospitalId);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.HospitalId.Should().Be(hospitalId);
        result.HospitalName.Should().Be("Independent Hospital");
        result.GSTIN.Should().BeNull();
        result.RegistrationNumber.Should().BeNull();
        result.PAN.Should().BeNull();
        result.NABHNumber.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WithNonExistentHospital_ShouldReturnNull()
    {
        // Arrange
        var hospitalId = Guid.NewGuid();

        var handler = new GetHospitalDetailsQueryHandler(_context);
        var query = new GetHospitalDetailsQuery(hospitalId);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ShouldIncludeAllHospitalProperties()
    {
        // Arrange
        var hospitalId = Guid.NewGuid();

        var hospital = new Hospital
        {
            HospitalId = hospitalId,
            HospitalName = "Complete Hospital",
            HospitalAddress = "789 Complete Road",
            GSTIN = "COMPLETE789",
            RegistrationNumber = "REG789",
            PAN = "PAN789",
            NABHNumber = "NABH789",
            Status = "Active"
        };

        _context.Hospitals.Add(hospital);
        await _context.SaveChangesAsync();

        var handler = new GetHospitalDetailsQueryHandler(_context);
        var query = new GetHospitalDetailsQuery(hospitalId);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.HospitalId.Should().Be(hospitalId);
        result.HospitalName.Should().Be("Complete Hospital");
        result.HospitalAddress.Should().Be("789 Complete Road");
        result.GSTIN.Should().Be("COMPLETE789");
        result.RegistrationNumber.Should().Be("REG789");
        result.PAN.Should().Be("PAN789");
        result.NABHNumber.Should().Be("NABH789");
        result.Status.Should().Be("Active");
    }
}
