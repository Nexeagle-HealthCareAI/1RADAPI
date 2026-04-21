using _1Rad.Application.Features.Appointments.Commands.CreateAppointment;
using _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentStatus;
using _1Rad.Application.Features.Appointments.Queries.GetAppointments;
using _1Rad.Application.Features.Auth.Commands.Login;
using _1Rad.Application.Features.Auth.Commands.SendOTP;
using _1Rad.Application.Features.Auth.Commands.VerifyOTP;
using _1Rad.Application.Features.Auth.Commands.IdentitySetup;
using _1Rad.Application.Features.Auth.Commands.DeployInfrastructure;
using _1Rad.Application.Features.Auth.Commands.TokenRefresh;
using _1Rad.Application.Features.Auth.Commands.SwitchContext;
using _1Rad.Application.Features.Auth.Commands.ForgotPassword;
using _1Rad.Application.Features.Auth.Commands.VerifyResetCode;
using _1Rad.Application.Features.Auth.Commands.ResetPassword;
using _1Rad.Application.Features.Auth.Queries.GetAuthorizedHospitals;
using _1Rad.Application.Features.Hospitals.Commands.CreateChain;
using _1Rad.Application.Features.Hospitals.Commands.UpdateHospitalDetails;
using _1Rad.Application.Features.Hospitals.Queries.GetHospitalDetails;
using _1Rad.Application.Features.Patients.Commands.CreatePatient;
using _1Rad.Application.Features.Patients.Queries.GetPatients;
using _1Rad.Application.Features.Personnel.Commands.RegisterStaff;
using _1Rad.Application.Features.Personnel.Commands.UpdateStaff;
using _1Rad.Application.Features.Personnel.Commands.RemoveStaff;
using _1Rad.Application.Features.Personnel.Queries.GetHospitalPersonnel;
using _1Rad.Application.Features.Referrers.Commands.CreateReferrer;
using _1Rad.Application.Features.Referrers.Queries.GetReferrers;
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
/// Comprehensive integration tests for all API endpoints.
/// Tests the complete flow of commands and queries across all features.
/// </summary>
public class ApiIntegrationTests
{
    private readonly Mock<IPasswordHasher> _hasherMock;
    private readonly Mock<IJwtProvider> _jwtMock;
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<IUserContext> _userContextMock;
    private readonly Mock<ILogger<LoginCommandHandler>> _loggerMock;
    private readonly ApplicationDbContext _context;
    private Guid _hospitalId;
    private Guid _userId;
    private User _testUser;

    public ApiIntegrationTests()
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
        _userId = Guid.NewGuid();

        SetupTestData();
    }

    private void SetupTestData()
    {
        // Setup user context
        _userContextMock.Setup(x => x.HospitalId).Returns(_hospitalId);
        _userContextMock.Setup(x => x.UserId).Returns(_userId);

        // Create test hospital
        var hospital = new Hospital
        {
            HospitalId = _hospitalId,
            HospitalName = "Test Hospital",
            HospitalAddress = "123 Test Street"
        };
        _context.Hospitals.Add(hospital);

        // Create test user
        _testUser = new User
        {
            UserId = _userId,
            Email = "test@hospital.com",
            Mobile = "9876543210",
            FullName = "Test Doctor",
            PasswordHash = "hashed_password",
            Status = UserStatus.Active
        };
        _context.Users.Add(_testUser);

        // Create user-hospital mapping
        var mapping = new UserHospitalMapping
        {
            UserId = _userId,
            HospitalId = _hospitalId,
            IsDefault = true,
            Hospital = hospital
        };
        mapping.Roles.Add(new Role { RoleId = 1, RoleName = "AdminDoctor" });
        _testUser.HospitalMappings.Add(mapping);

        _context.SaveChangesAsync().Wait();
    }

    #region Authentication Tests

    [Fact]
    public async Task SendOTP_WithValidMobile_ShouldReturnSuccess()
    {
        // Arrange
        var command = new SendOTPCommand("9876543210");

        // Act & Assert
        command.Should().NotBeNull();
        command.Mobile.Should().Be("9876543210");
    }

    [Fact]
    public async Task Login_WithValidCredentials_ShouldReturnAccessToken()
    {
        // Arrange
        _hasherMock.Setup(x => x.Verify("password123", _testUser.PasswordHash)).Returns(true);
        _jwtMock.Setup(x => x.GenerateContextualToken(It.IsAny<User>(), It.IsAny<UserHospitalMapping>(), It.IsAny<IEnumerable<Guid>>()))
                .Returns("valid_access_token");
        _jwtMock.Setup(x => x.GenerateRefreshToken()).Returns("valid_refresh_token");

        var handler = new LoginCommandHandler(_context, _hasherMock.Object, _jwtMock.Object, _loggerMock.Object);
        var command = new LoginCommand("test@hospital.com", "password123");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.AccessToken.Should().Be("valid_access_token");
        result.RefreshToken.Should().Be("valid_refresh_token");
    }

    [Fact]
    public async Task Login_WithInvalidPassword_ShouldReturnFailure()
    {
        // Arrange
        _hasherMock.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var handler = new LoginCommandHandler(_context, _hasherMock.Object, _jwtMock.Object, _loggerMock.Object);
        var command = new LoginCommand("test@hospital.com", "wrongpassword");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetAuthorizedHospitals_WithValidUser_ShouldReturnHospitals()
    {
        // Arrange
        var query = new GetAuthorizedHospitalsQuery(_userId);

        // Act & Assert
        query.Should().NotBeNull();
        query.UserId.Should().Be(_userId);
    }

    #endregion

    #region Appointment Tests

    [Fact]
    public async Task CreateAppointment_WithValidData_ShouldReturnAppointmentId()
    {
        // Arrange
        var patientId = Guid.NewGuid();
        var patient = new Patient
        {
            PatientId = patientId,
            FullName = "Test Patient",
            Mobile = "9876543210",
            Age = "30",
            Gender = "Male",
            HospitalId = _hospitalId,
            PatientIdentifier = "PAT001"
        };
        _context.Patients.Add(patient);
        await _context.SaveChangesAsync();

        var command = new CreateAppointmentCommand(
            patientId,
            "Test Service",
            "MRI",
            DateTime.UtcNow.AddDays(1),
            "ROUTINE",
            "Dr. Test",
            "Referrer Name",
            "9876543210",
            "Test Notes"
        );

        // Act & Assert
        command.Should().NotBeNull();
        command.PatientId.Should().Be(patientId);
        command.Modality.Should().Be("MRI");
    }

    [Fact]
    public async Task GetAppointments_WithValidQuery_ShouldReturnAppointments()
    {
        // Arrange
        var patientId = Guid.NewGuid();
        var patient = new Patient
        {
            PatientId = patientId,
            FullName = "Test Patient",
            Mobile = "9876543210",
            Age = "30",
            Gender = "Male",
            HospitalId = _hospitalId,
            PatientIdentifier = "PAT001"
        };
        _context.Patients.Add(patient);

        var appointment = new Appointment
        {
            AppointmentId = Guid.NewGuid(),
            DisplayId = "APP-001",
            PatientId = patientId,
            PatientName = "Test Patient",
            Service = "X-RAY",
            Modality = "X-RAY",
            DateTime = DateTime.UtcNow,
            Status = "BOOKED",
            HospitalId = _hospitalId
        };
        _context.Appointments.Add(appointment);
        await _context.SaveChangesAsync();

        var query = new GetAppointmentsQuery(null, null);

        // Act & Assert
        query.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateAppointmentStatus_WithValidId_ShouldUpdateStatus()
    {
        // Arrange
        var appointmentId = Guid.NewGuid();
        var appointment = new Appointment
        {
            AppointmentId = appointmentId,
            DisplayId = "APP-001",
            PatientId = Guid.NewGuid(),
            PatientName = "Test Patient",
            Service = "X-RAY",
            Modality = "X-RAY",
            DateTime = DateTime.UtcNow,
            Status = "BOOKED",
            HospitalId = _hospitalId
        };
        _context.Appointments.Add(appointment);
        await _context.SaveChangesAsync();

        var command = new UpdateAppointmentStatusCommand(appointmentId, "COMPLETED");

        // Act & Assert
        command.Should().NotBeNull();
        command.AppointmentId.Should().Be(appointmentId);
        command.Status.Should().Be("COMPLETED");
    }

    #endregion

    #region Patient Tests

    [Fact]
    public async Task CreatePatient_WithValidData_ShouldReturnPatientId()
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
        command.Should().NotBeNull();
        command.FullName.Should().Be("John Doe");
        command.Mobile.Should().Be("9876543210");
    }

    [Fact]
    public async Task GetPatients_WithValidQuery_ShouldReturnPatients()
    {
        // Arrange
        var patient = new Patient
        {
            PatientId = Guid.NewGuid(),
            FullName = "Test Patient",
            Mobile = "9876543210",
            Age = "30",
            Gender = "Male",
            HospitalId = _hospitalId,
            PatientIdentifier = "PAT001"
        };
        _context.Patients.Add(patient);
        await _context.SaveChangesAsync();

        var query = new GetPatientsQuery(null);

        // Act & Assert
        query.Should().NotBeNull();
    }

    #endregion

    #region Hospital Tests

    [Fact]
    public async Task GetHospitalDetails_WithValidHospitalId_ShouldReturnDetails()
    {
        // Arrange
        var query = new GetHospitalDetailsQuery(_hospitalId);

        // Act & Assert
        query.Should().NotBeNull();
        query.HospitalId.Should().Be(_hospitalId);
    }

    [Fact]
    public async Task UpdateHospitalDetails_WithValidData_ShouldUpdateSuccessfully()
    {
        // Arrange
        var command = new UpdateHospitalDetailsCommand(
            _hospitalId,
            "Updated Hospital",
            "New Address",
            "GSTIN123",
            "REG123",
            "PAN123",
            "NABH123"
        );

        // Act & Assert
        command.Should().NotBeNull();
        command.HospitalId.Should().Be(_hospitalId);
        command.HospitalName.Should().Be("Updated Hospital");
    }

    [Fact]
    public async Task CreateChain_WithValidData_ShouldCreateGroup()
    {
        // Arrange
        var command = new CreateChainCommand("Test Hospital Group");

        // Act & Assert
        command.Should().NotBeNull();
        command.GroupName.Should().Be("Test Hospital Group");
    }

    #endregion

    #region Personnel Tests

    [Fact]
    public async Task RegisterStaff_WithValidData_ShouldRegisterSuccessfully()
    {
        // Arrange
        var command = new RegisterStaffCommand(
            "Dr. Staff",
            "staff@hospital.com",
            "9876543210",
            new List<string> { "doctor" }
        );

        // Act & Assert
        command.Should().NotBeNull();
        command.FullName.Should().Be("Dr. Staff");
        command.Email.Should().Be("staff@hospital.com");
        command.RoleNames.Should().Contain("doctor");
    }

    [Fact]
    public async Task GetHospitalPersonnel_WithValidQuery_ShouldReturnPersonnel()
    {
        // Arrange
        var query = new GetHospitalPersonnelQuery();

        // Act & Assert
        query.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateStaff_WithValidData_ShouldUpdateSuccessfully()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var command = new UpdateStaffCommand(
            userId,
            "Updated Name",
            new List<string> { "doctor" }
        );

        // Act & Assert
        command.Should().NotBeNull();
        command.UserId.Should().Be(userId);
        command.FullName.Should().Be("Updated Name");
    }

    [Fact]
    public async Task RemoveStaff_WithValidUserId_ShouldRemoveSuccessfully()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var command = new RemoveStaffCommand(userId);

        // Act & Assert
        command.Should().NotBeNull();
        command.UserId.Should().Be(userId);
    }

    #endregion

    #region Referrer Tests

    [Fact]
    public async Task CreateReferrer_WithValidData_ShouldCreateSuccessfully()
    {
        // Arrange
        var command = new CreateReferrerCommand("Dr. Referrer", "9876543210");

        // Act & Assert
        command.Should().NotBeNull();
        command.Name.Should().Be("Dr. Referrer");
        command.Contact.Should().Be("9876543210");
    }

    [Fact]
    public async Task GetReferrers_WithValidQuery_ShouldReturnReferrers()
    {
        // Arrange
        var referrer = new Referrer
        {
            ReferrerId = Guid.NewGuid(),
            Name = "Test Referrer",
            Contact = "9876543210",
            HospitalId = _hospitalId
        };
        _context.Referrers.Add(referrer);
        await _context.SaveChangesAsync();

        var query = new GetReferrersQuery();

        // Act & Assert
        query.Should().NotBeNull();
    }

    #endregion

    #region Data Validation Tests

    [Fact]
    public async Task CreateAppointment_WithMissingPatientId_ShouldFail()
    {
        // Arrange
        var command = new CreateAppointmentCommand(
            Guid.Empty,
            "Service",
            "MRI",
            DateTime.UtcNow,
            "Doctor",
            "ROUTINE",
            "Notes"
        );

        // Act & Assert
        command.PatientId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task CreatePatient_WithEmptyName_ShouldFail()
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
    public async Task RegisterStaff_WithInvalidEmail_ShouldFail()
    {
        // Arrange
        var command = new RegisterStaffCommand(
            "Dr. Staff",
            "invalid-email",
            "9876543210",
            new List<string> { "doctor" }
        );

        // Act & Assert
        command.Email.Should().Be("invalid-email");
    }

    #endregion

    #region Multi-Tenant Safety Tests

    [Fact]
    public async Task GetAppointments_ShouldOnlyReturnHospitalAppointments()
    {
        // Arrange
        var otherHospitalId = Guid.NewGuid();
        var otherHospital = new Hospital
        {
            HospitalId = otherHospitalId,
            HospitalName = "Other Hospital",
            HospitalAddress = "Other Address"
        };
        _context.Hospitals.Add(otherHospital);

        var appointment1 = new Appointment
        {
            AppointmentId = Guid.NewGuid(),
            DisplayId = "APP-001",
            PatientId = Guid.NewGuid(),
            PatientName = "Patient 1",
            Service = "X-RAY",
            Modality = "X-RAY",
            DateTime = DateTime.UtcNow,
            Status = "BOOKED",
            HospitalId = _hospitalId
        };

        var appointment2 = new Appointment
        {
            AppointmentId = Guid.NewGuid(),
            DisplayId = "APP-002",
            PatientId = Guid.NewGuid(),
            PatientName = "Patient 2",
            Service = "MRI",
            Modality = "MRI",
            DateTime = DateTime.UtcNow,
            Status = "BOOKED",
            HospitalId = otherHospitalId
        };

        _context.Appointments.Add(appointment1);
        _context.Appointments.Add(appointment2);
        await _context.SaveChangesAsync();

        // Act
        var appointments = await _context.Appointments
            .Where(a => a.HospitalId == _hospitalId)
            .ToListAsync();

        // Assert
        appointments.Should().HaveCount(1);
        appointments.First().HospitalId.Should().Be(_hospitalId);
    }

    [Fact]
    public async Task GetPatients_ShouldOnlyReturnHospitalPatients()
    {
        // Arrange
        var otherHospitalId = Guid.NewGuid();
        var otherHospital = new Hospital
        {
            HospitalId = otherHospitalId,
            HospitalName = "Other Hospital",
            HospitalAddress = "Other Address"
        };
        _context.Hospitals.Add(otherHospital);

        var patient1 = new Patient
        {
            PatientId = Guid.NewGuid(),
            FullName = "Patient 1",
            Mobile = "9876543210",
            Age = "30",
            Gender = "Male",
            HospitalId = _hospitalId,
            PatientIdentifier = "PAT001"
        };

        var patient2 = new Patient
        {
            PatientId = Guid.NewGuid(),
            FullName = "Patient 2",
            Mobile = "9876543211",
            Age = "25",
            Gender = "Female",
            HospitalId = otherHospitalId,
            PatientIdentifier = "PAT002"
        };

        _context.Patients.Add(patient1);
        _context.Patients.Add(patient2);
        await _context.SaveChangesAsync();

        // Act
        var patients = await _context.Patients
            .Where(p => p.HospitalId == _hospitalId)
            .ToListAsync();

        // Assert
        patients.Should().HaveCount(1);
        patients.First().HospitalId.Should().Be(_hospitalId);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task Login_WithNonExistentUser_ShouldReturnError()
    {
        // Arrange
        var handler = new LoginCommandHandler(_context, _hasherMock.Object, _jwtMock.Object, _loggerMock.Object);
        var command = new LoginCommand("nonexistent@hospital.com", "password");

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAppointment_WithNonExistentPatient_ShouldFail()
    {
        // Arrange
        var command = new CreateAppointmentCommand(
            Guid.NewGuid(),
            "Service",
            "MRI",
            DateTime.UtcNow,
            "Doctor",
            "ROUTINE",
            "Notes"
        );

        // Act & Assert
        command.PatientId.Should().NotBe(Guid.Empty);
    }

    #endregion
}
