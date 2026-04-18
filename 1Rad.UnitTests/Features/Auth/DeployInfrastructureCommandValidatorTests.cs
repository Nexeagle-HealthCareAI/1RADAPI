using _1Rad.Application.Features.Auth.Commands.DeployInfrastructure;
using _1Rad.Domain.Constants;
using FluentAssertions;

namespace _1Rad.UnitTests.Features.Auth;

public class DeployInfrastructureCommandValidatorTests
{
    private readonly DeployInfrastructureCommandValidator _validator;

    public DeployInfrastructureCommandValidatorTests()
    {
        _validator = new DeployInfrastructureCommandValidator();
    }

    [Fact]
    public void Validate_WhenRoleIsAdminDoctorAndDegreeIsMissing_ShouldHaveValidationError()
    {
        // Arrange
        var command = new DeployInfrastructureCommand(
            Guid.NewGuid(), "Chain A", "Hospital 1", "Address 1", 
            RoleConstants.AdminDoctor, null, null, "LIC123");

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Degree");
    }

    [Fact]
    public void Validate_WhenRoleIsAdminDoctorAndFieldsArePresent_ShouldBeValid()
    {
        // Arrange
        var command = new DeployInfrastructureCommand(
            Guid.NewGuid(), "Chain A", "Hospital 1", "Address 1", 
            RoleConstants.AdminDoctor, "Radiology", "MBBS", "LIC123");

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenRoleIsAdminOperatorAndFieldsAreMissing_ShouldBeValid()
    {
        // Arrange
        var command = new DeployInfrastructureCommand(
            Guid.NewGuid(), "Chain A", "Hospital 1", "Address 1", 
            RoleConstants.AdminOperator, null, null, null);

        // Act
        var result = _validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }
}
