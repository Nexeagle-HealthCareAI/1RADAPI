using _1Rad.Application.Features.Hospitals.Commands.UpdateHospitalDetails;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace _1Rad.UnitTests.Features.Hospitals;

public class UpdateHospitalDetailsCommandHandlerTests
{
    private readonly Mock<IUserContext> _userContextMock;
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<ILogger<UpdateHospitalDetailsCommandHandler>> _loggerMock;
    private readonly ApplicationDbContext _context;

    public UpdateHospitalDetailsCommandHandlerTests()
    {
        _userContextMock = new Mock<IUserContext>();
        _publisherMock = new Mock<IPublisher>();
        _loggerMock = new Mock<ILogger<UpdateHospitalDetailsCommandHandler>>();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options, _publisherMock.Object, _userContextMock.Object);
    }

    [Fact]
    public async Task Handle_WithValidData_ShouldUpdateHospitalDetails()
    {
        // Arrange
        var hospitalId = Guid.NewGuid();
        var hospital = new Hospital
        {
            HospitalId = hospitalId,
            HospitalName = "Old Hospital Name",
            HospitalAddress = "Old Address",
            GSTIN = "OLD123456",
            Status = "Active"
        };
        _context.Hospitals.Add(hospital);
        await _context.SaveChangesAsync();

        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId);

        var handler = new UpdateHospitalDetailsCommandHandler(_context, _loggerMock.Object);
        var command = new UpdateHospitalDetailsCommand(
            hospitalId,
            "New Hospital Name",
            "New Address, City, State",
            "NEW789012",
            null,
            null,
            null,
            false);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();

        var updatedHospital = await _context.Hospitals.FindAsync(hospitalId);
        updatedHospital!.HospitalName.Should().Be("New Hospital Name");
        updatedHospital.HospitalAddress.Should().Be("New Address, City, State");
        updatedHospital.GSTIN.Should().Be("NEW789012");
    }

    [Fact]
    public async Task Handle_WithNonExistentHospital_ShouldReturnError()
    {
        // Arrange
        var hospitalId = Guid.NewGuid();
        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId);

        var handler = new UpdateHospitalDetailsCommandHandler(_context, _loggerMock.Object);
        var command = new UpdateHospitalDetailsCommand(
            hospitalId,
            "New Hospital Name",
            "New Address",
            null,
            null,
            null,
            null,
            false);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Handle_WithPartialUpdate_ShouldOnlyUpdateProvidedFields()
    {
        // Arrange
        var hospitalId = Guid.NewGuid();
        var hospital = new Hospital
        {
            HospitalId = hospitalId,
            HospitalName = "Original Name",
            HospitalAddress = "Original Address",
            GSTIN = "ORIGINAL123",
            Status = "Active"
        };
        _context.Hospitals.Add(hospital);
        await _context.SaveChangesAsync();

        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId);

        var handler = new UpdateHospitalDetailsCommandHandler(_context, _loggerMock.Object);
        var command = new UpdateHospitalDetailsCommand(
            hospitalId,
            "Updated Name",
            "Original Address",
            "ORIGINAL123",
            null,
            null,
            null,
            false);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();

        var updatedHospital = await _context.Hospitals.FindAsync(hospitalId);
        updatedHospital!.HospitalName.Should().Be("Updated Name");
        updatedHospital.HospitalAddress.Should().Be("Original Address");
        updatedHospital.GSTIN.Should().Be("ORIGINAL123");
    }

    [Fact]
    public async Task Handle_WithNullGSTIN_ShouldAllowNullValue()
    {
        // Arrange
        var hospitalId = Guid.NewGuid();
        var hospital = new Hospital
        {
            HospitalId = hospitalId,
            HospitalName = "Hospital Name",
            HospitalAddress = "Address",
            GSTIN = "GSTIN123",
            Status = "Active"
        };
        _context.Hospitals.Add(hospital);
        await _context.SaveChangesAsync();

        _userContextMock.Setup(x => x.HospitalId).Returns(hospitalId);

        var handler = new UpdateHospitalDetailsCommandHandler(_context, _loggerMock.Object);
        var command = new UpdateHospitalDetailsCommand(
            hospitalId,
            "Hospital Name",
            "Address",
            null,
            null,
            null,
            null,
            false);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();

        var updatedHospital = await _context.Hospitals.FindAsync(hospitalId);
        updatedHospital!.GSTIN.Should().BeNull();
    }
}
