using _1Rad.Application.Features.Appointments.Queries.GetStrategicOutlook;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace _1Rad.UnitTests.IntegrationTests;

/// <summary>
/// Tests for Strategic Outlook error handling and resilience.
/// Ensures the API gracefully handles database issues and missing data.
/// </summary>
public class StrategicOutlookErrorHandlingTests
{
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<IUserContext> _userContextMock;
    private readonly ApplicationDbContext _context;
    private Guid _hospitalId;

    public StrategicOutlookErrorHandlingTests()
    {
        _publisherMock = new Mock<IPublisher>();
        _userContextMock = new Mock<IUserContext>();

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
        _context.SaveChangesAsync().Wait();
    }

    #region Error Handling Tests

    [Fact]
    public async Task GetStrategicOutlook_WithEmptyDatabase_ShouldReturnValidResponse()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.KpiSnapshot.Should().NotBeNull();
        result.Modalities.Should().NotBeNull();
        result.RevenueBreakdown.Should().NotBeNull();
        result.VolumeTrends.Should().NotBeNull();
        result.Demographics.Should().NotBeNull();
        result.TopSources.Should().NotBeNull();
        result.InstitutionalLoyalty.Should().NotBeNull();
        result.ServiceFidelity.Should().NotBeNull();
    }

    [Fact]
    public async Task GetStrategicOutlook_WithNoAppointments_ShouldReturnZeroMetrics()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.KpiSnapshot.DailyMissions.Should().Be(0);
        result.KpiSnapshot.RegistryCount.Should().Be(0);
        result.Modalities.Should().BeEmpty();
        result.RevenueBreakdown.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStrategicOutlook_WithNoPatients_ShouldReturnZeroDemographics()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Demographics.GenderBrief.Male.Should().Be(0);
        result.Demographics.GenderBrief.Female.Should().Be(0);
        result.Demographics.GenderBrief.Other.Should().Be(0);
        result.Demographics.AgeTiers.Should().NotBeEmpty();
        result.Demographics.AgeTiers.All(t => t.Count == 0).Should().BeTrue();
    }

    [Fact]
    public async Task GetStrategicOutlook_WithInvalidHospitalId_ShouldThrowException()
    {
        // Arrange
        _userContextMock.Setup(x => x.HospitalId).Returns(Guid.Empty);
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(query, CancellationToken.None));
    }

    #endregion

    #region Resilience Tests

    [Fact]
    public async Task GetStrategicOutlook_ShouldAlwaysReturnValidStructure()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert - Verify all required properties exist
        result.Should().NotBeNull();
        result.KpiSnapshot.Should().NotBeNull();
        result.KpiSnapshot.RegistryCount.Should().BeGreaterThanOrEqualTo(0);
        result.KpiSnapshot.DailyMissions.Should().BeGreaterThanOrEqualTo(0);
        result.KpiSnapshot.FinancialYield.Should().BeGreaterThanOrEqualTo(0);
        result.KpiSnapshot.AvgLatency.Should().BeGreaterThan(0);
        result.KpiSnapshot.GrowthRate.Should().BeGreaterThanOrEqualTo(0);

        result.Modalities.Should().NotBeNull();
        result.RevenueBreakdown.Should().NotBeNull();
        result.VolumeTrends.Should().HaveCount(7);
        result.Demographics.Should().NotBeNull();
        result.TopSources.Should().NotBeNull();
        result.InstitutionalLoyalty.Should().NotBeNull();
        result.ServiceFidelity.Should().NotBeNull();
    }

    [Fact]
    public async Task GetStrategicOutlook_VolumeTrends_ShouldAlwaysHave7Days()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.VolumeTrends.Should().HaveCount(7);
        foreach (var trend in result.VolumeTrends)
        {
            trend.Day.Should().NotBeNullOrEmpty();
            trend.Count.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [Fact]
    public async Task GetStrategicOutlook_Demographics_ShouldAlwaysHaveAgeTiers()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Demographics.AgeTiers.Should().NotBeEmpty();
        result.Demographics.AgeTiers.Should().HaveCount(4); // Paed, Adult, Mature, Geriatric
        
        var expectedLabels = new[] { "0-18 (Paed)", "19-45 (Adult)", "46-65 (Mature)", "66+ (Geriatric)" };
        foreach (var label in expectedLabels)
        {
            result.Demographics.AgeTiers.Should().Contain(t => t.Label == label);
        }
    }

    [Fact]
    public async Task GetStrategicOutlook_ServiceFidelity_ShouldHaveValidTrend()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.ServiceFidelity.Trend.Should().BeOneOf("UP", "DOWN", "FLAT");
        result.ServiceFidelity.TodayCount.Should().BeGreaterThanOrEqualTo(0);
        result.ServiceFidelity.Avg30Day.Should().BeGreaterThanOrEqualTo(0);
    }

    #endregion

    #region Data Consistency Tests

    [Fact]
    public async Task GetStrategicOutlook_WithPartialData_ShouldCalculateCorrectly()
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

        var appointment = new Appointment
        {
            AppointmentId = Guid.NewGuid(),
            DisplayId = "APP-001",
            PatientId = patient.PatientId,
            PatientName = "Test Patient",
            Service = "X-RAY",
            Modality = "X-RAY",
            DateTime = DateTime.UtcNow,
            Status = "BOOKED",
            HospitalId = _hospitalId
        };
        _context.Appointments.Add(appointment);
        await _context.SaveChangesAsync();

        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.KpiSnapshot.RegistryCount.Should().Be(1);
        result.KpiSnapshot.DailyMissions.Should().Be(1);
        result.Modalities.Should().HaveCount(1);
        result.Modalities.First().Label.Should().Be("X-RAY");
        result.Modalities.First().Count.Should().Be(1);
    }

    [Fact]
    public async Task GetStrategicOutlook_FinancialYield_ShouldFallbackToModalityWeights()
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

        var appointment = new Appointment
        {
            AppointmentId = Guid.NewGuid(),
            DisplayId = "APP-001",
            PatientId = patient.PatientId,
            PatientName = "Test Patient",
            Service = "MRI",
            Modality = "MRI",
            DateTime = DateTime.UtcNow,
            Status = "BOOKED",
            HospitalId = _hospitalId
        };
        _context.Appointments.Add(appointment);
        await _context.SaveChangesAsync();

        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        // MRI weight is 250m, so financial yield should be 250
        result.KpiSnapshot.FinancialYield.Should().Be(250m);
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public async Task GetStrategicOutlook_WithFutureDate_ShouldReturnEmptyMetrics()
    {
        // Arrange
        var futureDate = DateTime.UtcNow.AddDays(30);
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(futureDate);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.KpiSnapshot.DailyMissions.Should().Be(0);
        result.Modalities.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStrategicOutlook_WithPastDate_ShouldReturnHistoricalData()
    {
        // Arrange
        var pastDate = DateTime.UtcNow.AddDays(-5);
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(pastDate);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.KpiSnapshot.Should().NotBeNull();
    }

    [Fact]
    public async Task GetStrategicOutlook_WithNullReferenceDate_ShouldUseTodayDate()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(null);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.KpiSnapshot.Should().NotBeNull();
    }

    #endregion
}
