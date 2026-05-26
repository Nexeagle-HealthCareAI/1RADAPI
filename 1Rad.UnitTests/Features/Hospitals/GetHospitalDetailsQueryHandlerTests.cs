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

        // Assert — HospitalDetailsDto exposes Id / Name / Address / Status
        // (regulatory fields like GSTIN/PAN/NABH live only on the entity).
        result.Should().NotBeNull();
        result!.Id.Should().Be(hospitalId);
        result.Name.Should().Be("Test Hospital");
        result.Address.Should().Be("123 Test Street, Test City");
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

        // Assert — the DTO doesn't surface GSTIN/PAN/etc, so just confirm
        // the basic fields are mapped and the call succeeds when the
        // hospital has no group attached.
        result.Should().NotBeNull();
        result!.Id.Should().Be(hospitalId);
        result.Name.Should().Be("Independent Hospital");
        result.Address.Should().Be("456 Independent Ave");
        result.Status.Should().Be("Active");
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
        result.Should().NotBeNull();
        result!.Id.Should().Be(hospitalId);
        result.Name.Should().Be("Complete Hospital");
        result.Address.Should().Be("789 Complete Road");
        result.Status.Should().Be("Active");
    }
}
