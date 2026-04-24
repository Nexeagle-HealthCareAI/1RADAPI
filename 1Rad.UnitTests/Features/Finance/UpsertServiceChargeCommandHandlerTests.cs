using _1Rad.Application.Features.Finance.Commands.UpsertServiceCharge;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace _1Rad.UnitTests.Features.Finance;

public class UpsertServiceChargeCommandHandlerTests : BaseHandlerTest
{
    private readonly UpsertServiceChargeCommandHandler _handler;

    public UpsertServiceChargeCommandHandlerTests()
    {
        _handler = new UpsertServiceChargeCommandHandler(Context);
    }

    [Fact]
    public async Task Handle_CreateNewServiceCharge_ReturnsId()
    {
        // Arrange
        var command = new UpsertServiceChargeCommand
        {
            ServiceName = "X-Ray Chest",
            Amount = 500m,
            Modality = "X-RAY"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotEqual(Guid.Empty, result);
        
        var serviceCharge = await Context.ServiceCharges.FindAsync(result);
        Assert.NotNull(serviceCharge);
        Assert.Equal("X-Ray Chest", serviceCharge.ServiceName);
        Assert.Equal(500m, serviceCharge.Amount);
        Assert.Equal("X-RAY", serviceCharge.Modality);
        Assert.Equal(HospitalId, serviceCharge.HospitalId);
    }

    [Fact]
    public async Task Handle_UpdateExistingServiceCharge_ReturnsId()
    {
        // Arrange
        var existingId = Guid.NewGuid();
        var existing = new ServiceCharge
        {
            Id = existingId,
            ServiceName = "X-Ray Chest",
            Amount = 500m,
            HospitalId = HospitalId,
            Modality = "X-RAY"
        };

        Context.ServiceCharges.Add(existing);
        await Context.SaveChangesAsync();

        var command = new UpsertServiceChargeCommand
        {
            Id = existingId,
            ServiceName = "X-Ray Chest PA View",
            Amount = 600m, // Updated amount
            Modality = "X-RAY"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.Equal(existingId, result);
        
        var updated = await Context.ServiceCharges.FindAsync(existingId);
        Assert.NotNull(updated);
        Assert.Equal(600m, updated.Amount);
        Assert.Equal("X-Ray Chest PA View", updated.ServiceName);
    }

    [Fact]
    public async Task Handle_InvalidAmount_ThrowsArgumentException()
    {
        // Arrange
        var command = new UpsertServiceChargeCommand
        {
            ServiceName = "X-Ray",
            Amount = 0m, // Invalid
            Modality = "X-RAY"
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.Handle(command, CancellationToken.None));
        Assert.Contains("Amount must be greater than zero", exception.Message);
    }

    [Fact]
    public async Task Handle_NegativeAmount_ThrowsArgumentException()
    {
        // Arrange
        var command = new UpsertServiceChargeCommand
        {
            ServiceName = "X-Ray",
            Amount = -100m,
            Modality = "X-RAY"
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.Handle(command, CancellationToken.None));
        Assert.Contains("Amount must be greater than zero", exception.Message);
    }

    [Fact]
    public async Task Handle_MissingServiceName_ThrowsArgumentException()
    {
        // Arrange
        var command = new UpsertServiceChargeCommand
        {
            ServiceName = "",
            Amount = 500m,
            Modality = "X-RAY"
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.Handle(command, CancellationToken.None));
        Assert.Contains("Service name is required", exception.Message);
    }

    [Fact]
    public async Task Handle_MissingModality_ThrowsArgumentException()
    {
        // Arrange
        var command = new UpsertServiceChargeCommand
        {
            ServiceName = "X-Ray",
            Amount = 500m,
            Modality = ""
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.Handle(command, CancellationToken.None));
        Assert.Contains("Modality is required", exception.Message);
    }

    [Fact]
    public async Task Handle_EmptyHospitalContext_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        MockUserContext.Setup(x => x.HospitalId).Returns(Guid.Empty);

        var command = new UpsertServiceChargeCommand
        {
            ServiceName = "X-Ray",
            Amount = 500m,
            Modality = "X-RAY"
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _handler.Handle(command, CancellationToken.None));
        Assert.Contains("Hospital context is required", exception.Message);
    }

    [Fact]
    public async Task Handle_UpdateDifferentHospitalServiceCharge_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var existingId = Guid.NewGuid();
        var existing = new ServiceCharge
        {
            Id = existingId,
            ServiceName = "X-Ray",
            Amount = 500m,
            HospitalId = Guid.NewGuid(), // Different hospital
            Modality = "X-RAY"
        };

        Context.ServiceCharges.Add(existing);
        await Context.SaveChangesAsync();

        var command = new UpsertServiceChargeCommand
        {
            Id = existingId,
            ServiceName = "X-Ray",
            Amount = 600m,
            Modality = "X-RAY"
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _handler.Handle(command, CancellationToken.None));
        Assert.Contains("You do not have permission to modify this service charge", exception.Message);
    }

    [Fact]
    public async Task Handle_UpdateNonExistentServiceCharge_ThrowsKeyNotFoundException()
    {
        // Arrange
        var command = new UpsertServiceChargeCommand
        {
            Id = Guid.NewGuid(), // Non-existent ID
            ServiceName = "X-Ray",
            Amount = 500m,
            Modality = "X-RAY"
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _handler.Handle(command, CancellationToken.None));
        Assert.Contains("not found", exception.Message);
    }

    [Fact]
    public async Task Handle_DuplicateServiceCharge_ThrowsInvalidOperationException()
    {
        // Arrange
        var existing = new ServiceCharge
        {
            Id = Guid.NewGuid(),
            ServiceName = "X-Ray Chest",
            Amount = 500m,
            HospitalId = HospitalId,
            Modality = "X-RAY"
        };

        Context.ServiceCharges.Add(existing);
        await Context.SaveChangesAsync();

        var command = new UpsertServiceChargeCommand
        {
            ServiceName = "X-Ray Chest", // Same name and modality
            Amount = 600m,
            Modality = "X-RAY"
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _handler.Handle(command, CancellationToken.None));
        Assert.Contains("already exists", exception.Message);
    }

    [Fact]
    public async Task Handle_DifferentModalitySameName_CreatesSuccessfully()
    {
        // Arrange
        var existing = new ServiceCharge
        {
            Id = Guid.NewGuid(),
            ServiceName = "Chest Scan",
            Amount = 500m,
            HospitalId = HospitalId,
            Modality = "X-RAY"
        };

        Context.ServiceCharges.Add(existing);
        await Context.SaveChangesAsync();

        var command = new UpsertServiceChargeCommand
        {
            ServiceName = "Chest Scan", // Same name but different modality
            Amount = 3000m,
            Modality = "CT-SCAN"
        };

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotEqual(Guid.Empty, result);
        Assert.NotEqual(existing.Id, result);
        
        var allCharges = await Context.ServiceCharges
            .Where(x => x.ServiceName == "Chest Scan")
            .ToListAsync();
        Assert.Equal(2, allCharges.Count);
    }
}
