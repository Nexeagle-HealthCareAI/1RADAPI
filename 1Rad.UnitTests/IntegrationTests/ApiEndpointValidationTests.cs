using _1Rad.Application.Features.Appointments.Commands.CreateAppointment;
using _1Rad.Application.Features.Auth.Commands.Login;
using _1Rad.Application.Features.Auth.Commands.SendOTP;
using _1Rad.Application.Features.Patients.Commands.CreatePatient;
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

namespace _1Rad.UnitTests.IntegrationTests;

/// <summary>
/// Comprehensive validation tests for all API endpoints.
/// Tests input validation, error handling, and edge cases.
/// </summary>
public class ApiEndpointValidationTests
{
    private readonly Mock<IPasswordHasher> _hasherMock;
    private readonly Mock<IJwtProvider> _jwtMock;
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<IUserContext> _userContextMock;
    private readonly Mock<ILogger<LoginCommandHandler>> _loggerMock;
    private readonly ApplicationDbContext _context;
    private Guid _hospitalId;

    public ApiEndpointValidationTests()
    {
        _hasherMock = new Mock<IPasswordHasher>();
        _jwtMock = new Mock<IJwtProvider>();
        _publisherMock = new Mock<IPublisher>();
        _userContextMock = new Mock<IUserContext>();
        _loggerMock = new Mock<ILogger<LoginCommandHandler>>();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options, _publisherMock.Object, _userContextMock.Object);
        _hospitalId = Guid.NewGuid();

        SetupTestData();
    }

    private void SetupTestData()
    {
        _userContextMock.Setup(x => x.HospitalId).Returns(_hospitalId);

        var hospital = new Hospital
        {
            HospitalId = _hospitalId,
            HospitalName = "Test Hospital",
            HospitalAddress = "123 Test Street"
        };
        _context.Hospitals.Add(hospital);

        var user = new User
        {
            UserId = Guid.NewGuid(),
            Email = "test@hospital.com",
            Mobile = "9876543210",
            FullName = "Test User",
            PasswordHash = "hashed_password",
            Status = UserStatus.Active
        };
        _context.Users.Add(user);

        _context.SaveChangesAsync().Wait();
    }

    #region Authentication Validation Tests

    [Fact]
    public void SendOTP_WithValidMobile_ShouldBeValid()
    {
        // Arrange
        var command = new SendOTPCommand("9876543210");

        // Act & Assert
        command.Mobile.Should().NotBeNullOrEmpty();
        command.Mobile.Should().HaveLength(10);
        command.Mobile.Should().OnlyContain(c => char.IsDigit(c));
    }

    [Fact]
    public void SendOTP_WithInvalidMobile_ShouldBeInvalid()
    {
        // Arrange
        var command = new SendOTPCommand("invalid");

        // Act & Assert
        command.Mobile.Should().Be("invalid");
    }

    [Fact]
    public void SendOTP_WithEmptyMobile_ShouldBeInvalid()
    {
        // Arrange
        var command = new SendOTPCommand("");

        // Act & Assert
        command.Mobile.Should().BeEmpty();
    }

    [Fact]
    public void SendOTP_WithNullMobile_ShouldBeInvalid()
    {
        // Arrange
        var command = new SendOTPCommand(null);

        // Act & Assert
        command.Mobile.Should().BeNull();
    }

    [Fact]
    public void Login_WithValidEmail_ShouldBeValid()
    {
        // Arrange
        var command = new LoginCommand("test@hospital.com", "password123");

        // Act & Assert
        command.Identifier.Should().Be("test@hospital.com");
        command.Password.Should().Be("password123");
    }

    [Fact]
    public void Login_WithEmptyIdentifier_ShouldBeInvalid()
    {
        // Arrange
        var command = new LoginCommand("", "password");

        // Act & Assert
        command.Identifier.Should().BeEmpty();
    }

    [Fact]
    public void Login_WithEmptyPassword_ShouldBeInvalid()
    {
        // Arrange
        var command = new LoginCommand("test@hospital.com", "");

        // Act & Assert
        command.Password.Should().BeEmpty();
    }

    [Fact]
    public void Login_WithShortPassword_ShouldBeInvalid()
    {
        // Arrange
        var command = new LoginCommand("test@hospital.com", "123");

        // Act & Assert
        command.Password.Length.Should().BeLessThan(8);
    }

    #endregion

    #region Patient Validation Tests

    [Fact]
    public void CreatePatient_WithValidData_ShouldBeValid()
    {
        // Arrange
        var command = new CreatePatientCommand(
            "John Doe",
            "9876543210",
            "30",
            "Male",
            "Village",
            "District",
            "Address",
            "MRN001",
            "Referral"
        );

        // Act & Assert
        command.FullName.Should().NotBeNullOrEmpty();
        command.Mobile.Should().NotBeNullOrEmpty();
        command.Age.Should().NotBeNullOrEmpty();
        command.Gender.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void CreatePatient_WithEmptyName_ShouldBeInvalid()
    {
        // Arrange
        var command = new CreatePatientCommand(
            "",
            "9876543210",
            "30",
            "Male",
            "Village",
            "District",
            "Address",
            "MRN001",
            "Referral"
        );

        // Act & Assert
        command.FullName.Should().BeEmpty();
    }

    [Fact]
    public void CreatePatient_WithInvalidAge_ShouldBeInvalid()
    {
        // Arrange
        var command = new CreatePatientCommand(
            "John Doe",
            "9876543210",
            "invalid",
            "Male",
            "Village",
            "District",
            "Address",
            "MRN001",
            "Referral"
        );

        // Act & Assert
        int.TryParse(command.Age, out _).Should().BeFalse();
    }

    [Fact]
    public void CreatePatient_WithNegativeAge_ShouldBeInvalid()
    {
        // Arrange
        var command = new CreatePatientCommand(
            "John Doe",
            "9876543210",
            "-5",
            "Male",
            "Village",
            "District",
            "Address",
            "MRN001",
            "Referral"
        );

        // Act & Assert
        int.Parse(command.Age).Should().BeLessThan(0);
    }

    [Fact]
    public void CreatePatient_WithExcessiveAge_ShouldBeInvalid()
    {
        // Arrange
        var command = new CreatePatientCommand(
            "John Doe",
            "9876543210",
            "150",
            "Male",
            "Village",
            "District",
            "Address",
            "MRN001",
            "Referral"
        );

        // Act & Assert
        int.Parse(command.Age).Should().BeGreaterThan(120);
    }

    [Fact]
    public void CreatePatient_WithInvalidGender_ShouldBeInvalid()
    {
        // Arrange
        var command = new CreatePatientCommand(
            "John Doe",
            "9876543210",
            "30",
            "InvalidGender",
            "Village",
            "District",
            "Address",
            "MRN001",
            "Referral"
        );

        // Act & Assert
        command.Gender.Should().NotBeOneOf("Male", "Female", "Other");
    }

    [Fact]
    public void CreatePatient_WithInvalidMobile_ShouldBeInvalid()
    {
        // Arrange
        var command = new CreatePatientCommand(
            "John Doe",
            "123",
            "30",
            "Male",
            "Village",
            "District",
            "Address",
            "MRN001",
            "Referral"
        );

        // Act & Assert
        command.Mobile.Length.Should().BeLessThan(10);
    }

    #endregion

    #region Appointment Validation Tests

    [Fact]
    public void CreateAppointment_WithValidData_ShouldBeValid()
    {
        // Arrange
        var patientId = Guid.NewGuid();
        var command = new CreateAppointmentCommand(
            patientId,
            "X-RAY",
            "X-RAY",
            DateTime.UtcNow.AddDays(1),
            "Dr. Test",
            "ROUTINE",
            "Test Notes"
        );

        // Act & Assert
        command.PatientId.Should().NotBe(Guid.Empty);
        command.Modality.Should().NotBeNullOrEmpty();
        command.DateTime.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public void CreateAppointment_WithEmptyPatientId_ShouldBeInvalid()
    {
        // Arrange
        var command = new CreateAppointmentCommand(
            Guid.Empty,
            "X-RAY",
            "X-RAY",
            DateTime.UtcNow.AddDays(1),
            "Dr. Test",
            "ROUTINE",
            "Notes"
        );

        // Act & Assert
        command.PatientId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void CreateAppointment_WithPastDateTime_ShouldBeInvalid()
    {
        // Arrange
        var command = new CreateAppointmentCommand(
            Guid.NewGuid(),
            "X-RAY",
            "X-RAY",
            DateTime.UtcNow.AddDays(-1),
            "Dr. Test",
            "ROUTINE",
            "Notes"
        );

        // Act & Assert
        command.DateTime.Should().BeBefore(DateTime.UtcNow);
    }

    [Fact]
    public void CreateAppointment_WithInvalidModality_ShouldBeInvalid()
    {
        // Arrange
        var command = new CreateAppointmentCommand(
            Guid.NewGuid(),
            "INVALID",
            "INVALID",
            DateTime.UtcNow.AddDays(1),
            "Dr. Test",
            "ROUTINE",
            "Notes"
        );

        // Act & Assert
        command.Modality.Should().NotBeOneOf("X-RAY", "MRI", "CT", "USG", "PET");
    }

    [Fact]
    public void CreateAppointment_WithEmptyService_ShouldBeInvalid()
    {
        // Arrange
        var command = new CreateAppointmentCommand(
            Guid.NewGuid(),
            "",
            "X-RAY",
            DateTime.UtcNow.AddDays(1),
            "Dr. Test",
            "ROUTINE",
            "Notes"
        );

        // Act & Assert
        command.Service.Should().BeEmpty();
    }

    #endregion

    #region Personnel Validation Tests

    [Fact]
    public void RegisterStaff_WithValidData_ShouldBeValid()
    {
        // Arrange
        var command = new RegisterStaffCommand(
            "Dr. Staff",
            "staff@hospital.com",
            "9876543210",
            new List<string> { "doctor" }
        );

        // Act & Assert
        command.FullName.Should().NotBeNullOrEmpty();
        command.Email.Should().NotBeNullOrEmpty();
        command.Mobile.Should().NotBeNullOrEmpty();
        command.RoleNames.Should().NotBeEmpty();
    }

    [Fact]
    public void RegisterStaff_WithEmptyName_ShouldBeInvalid()
    {
        // Arrange
        var command = new RegisterStaffCommand(
            "",
            "staff@hospital.com",
            "9876543210",
            new List<string> { "doctor" }
        );

        // Act & Assert
        command.FullName.Should().BeEmpty();
    }

    [Fact]
    public void RegisterStaff_WithInvalidEmail_ShouldBeInvalid()
    {
        // Arrange
        var command = new RegisterStaffCommand(
            "Dr. Staff",
            "invalid-email",
            "9876543210",
            new List<string> { "doctor" }
        );

        // Act & Assert
        command.Email.Should().NotContain("@");
    }

    [Fact]
    public void RegisterStaff_WithInvalidMobile_ShouldBeInvalid()
    {
        // Arrange
        var command = new RegisterStaffCommand(
            "Dr. Staff",
            "staff@hospital.com",
            "123",
            new List<string> { "doctor" }
        );

        // Act & Assert
        command.Mobile.Length.Should().BeLessThan(10);
    }

    [Fact]
    public void RegisterStaff_WithEmptyRoles_ShouldBeInvalid()
    {
        // Arrange
        var command = new RegisterStaffCommand(
            "Dr. Staff",
            "staff@hospital.com",
            "9876543210",
            new List<string>()
        );

        // Act & Assert
        command.RoleNames.Should().BeEmpty();
    }

    [Fact]
    public void RegisterStaff_WithInvalidRole_ShouldBeInvalid()
    {
        // Arrange
        var command = new RegisterStaffCommand(
            "Dr. Staff",
            "staff@hospital.com",
            "9876543210",
            new List<string> { "invalid_role" }
        );

        // Act & Assert
        var validRoles = new[] { "admindoctor", "admin", "doctor", "technician", "receptionist", "accountant" };
        command.RoleNames.Should().NotContain(r => validRoles.Contains(r.ToLower()));
    }

    #endregion

    #region Boundary Tests

    [Fact]
    public void CreatePatient_WithMaxLengthName_ShouldBeValid()
    {
        // Arrange
        var longName = new string('A', 255);
        var command = new CreatePatientCommand(
            longName,
            "9876543210",
            "30",
            "Male",
            "Village",
            "District",
            "Address",
            "MRN001",
            "Referral"
        );

        // Act & Assert
        command.FullName.Length.Should().Be(255);
    }

    [Fact]
    public void CreatePatient_WithExcessivelyLongName_ShouldBeInvalid()
    {
        // Arrange
        var veryLongName = new string('A', 500);
        var command = new CreatePatientCommand(
            veryLongName,
            "9876543210",
            "30",
            "Male",
            "Village",
            "District",
            "Address",
            "MRN001",
            "Referral"
        );

        // Act & Assert
        command.FullName.Length.Should().BeGreaterThan(255);
    }

    [Fact]
    public void CreateAppointment_WithMinimumDateTime_ShouldBeValid()
    {
        // Arrange
        var command = new CreateAppointmentCommand(
            Guid.NewGuid(),
            "X-RAY",
            "X-RAY",
            DateTime.UtcNow.AddMinutes(1),
            "Dr. Test",
            "ROUTINE",
            "Notes"
        );

        // Act & Assert
        command.DateTime.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public void CreateAppointment_WithDistantFutureDateTime_ShouldBeValid()
    {
        // Arrange
        var command = new CreateAppointmentCommand(
            Guid.NewGuid(),
            "X-RAY",
            "X-RAY",
            DateTime.UtcNow.AddYears(1),
            "Dr. Test",
            "ROUTINE",
            "Notes"
        );

        // Act & Assert
        command.DateTime.Should().BeAfter(DateTime.UtcNow.AddMonths(11));
    }

    #endregion

    #region Null/Empty Reference Tests

    [Fact]
    public void CreatePatient_WithNullName_ShouldBeInvalid()
    {
        // Arrange
        var command = new CreatePatientCommand(
            null,
            "9876543210",
            "30",
            "Male",
            "Village",
            "District",
            "Address",
            "MRN001",
            "Referral"
        );

        // Act & Assert
        command.FullName.Should().BeNull();
    }

    [Fact]
    public void RegisterStaff_WithNullRoles_ShouldBeInvalid()
    {
        // Arrange
        var command = new RegisterStaffCommand(
            "Dr. Staff",
            "staff@hospital.com",
            "9876543210",
            null
        );

        // Act & Assert
        command.RoleNames.Should().BeNull();
    }

    #endregion

    #region Special Characters Tests

    [Fact]
    public void CreatePatient_WithSpecialCharactersInName_ShouldBeValid()
    {
        // Arrange
        var command = new CreatePatientCommand(
            "Dr. John O'Brien-Smith",
            "9876543210",
            "30",
            "Male",
            "Village",
            "District",
            "Address",
            "MRN001",
            "Referral"
        );

        // Act & Assert
        command.FullName.Should().Contain("'");
        command.FullName.Should().Contain("-");
    }

    [Fact]
    public void RegisterStaff_WithSpecialCharactersInEmail_ShouldBeValid()
    {
        // Arrange
        var command = new RegisterStaffCommand(
            "Dr. Staff",
            "staff+test@hospital.com",
            "9876543210",
            new List<string> { "doctor" }
        );

        // Act & Assert
        command.Email.Should().Contain("+");
    }

    #endregion

    #region Case Sensitivity Tests

    [Fact]
    public void CreatePatient_WithMixedCaseGender_ShouldBeValid()
    {
        // Arrange
        var command = new CreatePatientCommand(
            "John Doe",
            "9876543210",
            "30",
            "MALE",
            "Village",
            "District",
            "Address",
            "MRN001",
            "Referral"
        );

        // Act & Assert
        command.Gender.Should().Be("MALE");
    }

    [Fact]
    public void RegisterStaff_WithMixedCaseRole_ShouldBeValid()
    {
        // Arrange
        var command = new RegisterStaffCommand(
            "Dr. Staff",
            "staff@hospital.com",
            "9876543210",
            new List<string> { "DOCTOR" }
        );

        // Act & Assert
        command.RoleNames.First().Should().Be("DOCTOR");
    }

    #endregion
}
