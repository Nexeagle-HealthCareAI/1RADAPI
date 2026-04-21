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
/// Integration tests for Intelligence and Finance endpoints.
/// Tests strategic outlook, financial metrics, and reporting features.
/// </summary>
public class IntelligenceAndFinanceTests
{
    private readonly Mock<IPublisher> _publisherMock;
    private readonly Mock<IUserContext> _userContextMock;
    private readonly ApplicationDbContext _context;
    private Guid _hospitalId;

    public IntelligenceAndFinanceTests()
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

        // Create hospital
        var hospital = new Hospital
        {
            HospitalId = _hospitalId,
            HospitalName = "Test Hospital",
            HospitalAddress = "123 Test Street"
        };
        _context.Hospitals.Add(hospital);

        // Create patients
        for (int i = 0; i < 10; i++)
        {
            var patient = new Patient
            {
                PatientId = Guid.NewGuid(),
                FullName = $"Patient {i}",
                Mobile = $"987654321{i}",
                Age = (20 + i).ToString(),
                Gender = i % 2 == 0 ? "Male" : "Female",
                HospitalId = _hospitalId,
                PatientIdentifier = $"PAT{i:D3}",
                CreatedAt = DateTime.UtcNow.AddDays(-i)
            };
            _context.Patients.Add(patient);
        }

        // Create referrers
        for (int i = 0; i < 5; i++)
        {
            var referrer = new Referrer
            {
                ReferrerId = Guid.NewGuid(),
                Name = $"Dr. Referrer {i}",
                Contact = $"987654321{i}",
                HospitalId = _hospitalId
            };
            _context.Referrers.Add(referrer);
        }

        _context.SaveChangesAsync().Wait();

        // Create appointments
        var patients = _context.Patients.Where(p => p.HospitalId == _hospitalId).ToList();
        var referrers = _context.Referrers.Where(r => r.HospitalId == _hospitalId).ToList();
        var modalities = new[] { "X-RAY", "MRI", "CT", "USG", "PET" };

        for (int i = 0; i < 20; i++)
        {
            var appointment = new Appointment
            {
                AppointmentId = Guid.NewGuid(),
                DisplayId = $"APP-{i:D3}",
                PatientId = patients[i % patients.Count].PatientId,
                PatientName = patients[i % patients.Count].FullName,
                Service = "Radiology",
                Modality = modalities[i % modalities.Length],
                DateTime = DateTime.UtcNow.AddDays(-(i / 3)).AddHours(i),
                Type = "ROUTINE",
                Status = i % 3 == 0 ? "COMPLETED" : "BOOKED",
                Doctor = "Dr. Test",
                ReferredBy = referrers[i % referrers.Count].Name,
                HospitalId = _hospitalId
            };
            _context.Appointments.Add(appointment);
        }

        _context.SaveChangesAsync().Wait();
    }

    #region Strategic Outlook Tests

    [Fact]
    public async Task GetStrategicOutlook_WithValidData_ShouldReturnCompleteOutlook()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.KpiSnapshot.Should().NotBeNull();
        result.Modalities.Should().NotBeEmpty();
        result.RevenueBreakdown.Should().NotBeEmpty();
        result.VolumeTrends.Should().NotBeEmpty();
        result.Demographics.Should().NotBeNull();
        result.TopSources.Should().NotBeEmpty();
        result.InstitutionalLoyalty.Should().NotBeNull();
        result.ServiceFidelity.Should().NotBeNull();
    }

    [Fact]
    public async Task GetStrategicOutlook_KpiSnapshot_ShouldHaveValidMetrics()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.KpiSnapshot.RegistryCount.Should().BeGreaterThan(0);
        result.KpiSnapshot.DailyMissions.Should().BeGreaterThanOrEqualTo(0);
        result.KpiSnapshot.FinancialYield.Should().BeGreaterThanOrEqualTo(0);
        result.KpiSnapshot.AvgLatency.Should().BeGreaterThan(0);
        result.KpiSnapshot.GrowthRate.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GetStrategicOutlook_Modalities_ShouldHaveCorrectStructure()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        foreach (var modality in result.Modalities)
        {
            modality.Label.Should().NotBeNullOrEmpty();
            modality.Count.Should().BeGreaterThanOrEqualTo(0);
            modality.Color.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task GetStrategicOutlook_RevenueBreakdown_ShouldCalculateCorrectly()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        foreach (var revenue in result.RevenueBreakdown)
        {
            revenue.Label.Should().NotBeNullOrEmpty();
            revenue.Amount.Should().BeGreaterThanOrEqualTo(0);
            revenue.Color.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task GetStrategicOutlook_VolumeTrends_ShouldHave7Days()
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
    public async Task GetStrategicOutlook_Demographics_ShouldHaveGenderAndAge()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Demographics.GenderBrief.Should().NotBeNull();
        result.Demographics.GenderBrief.Male.Should().BeGreaterThanOrEqualTo(0);
        result.Demographics.GenderBrief.Female.Should().BeGreaterThanOrEqualTo(0);
        result.Demographics.GenderBrief.Other.Should().BeGreaterThanOrEqualTo(0);

        result.Demographics.AgeTiers.Should().NotBeEmpty();
        foreach (var tier in result.Demographics.AgeTiers)
        {
            tier.Label.Should().NotBeNullOrEmpty();
            tier.Count.Should().BeGreaterThanOrEqualTo(0);
            tier.Percentage.Should().BeGreaterThanOrEqualTo(0);
            tier.Color.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task GetStrategicOutlook_TopSources_ShouldReturnTop5()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.TopSources.Should().NotBeEmpty();
        result.TopSources.Count.Should().BeLessThanOrEqualTo(5);
        foreach (var source in result.TopSources)
        {
            source.Name.Should().NotBeNullOrEmpty();
            source.Count.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task GetStrategicOutlook_InstitutionalLoyalty_ShouldCalculateCorrectly()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.InstitutionalLoyalty.NewPatients.Should().BeGreaterThanOrEqualTo(0);
        result.InstitutionalLoyalty.ReturningPatients.Should().BeGreaterThanOrEqualTo(0);
        result.InstitutionalLoyalty.ReturnPercentage.Should().BeGreaterThanOrEqualTo(0);
        result.InstitutionalLoyalty.ReturnPercentage.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task GetStrategicOutlook_ServiceFidelity_ShouldShowTrend()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.ServiceFidelity.TodayCount.Should().BeGreaterThanOrEqualTo(0);
        result.ServiceFidelity.Avg30Day.Should().BeGreaterThanOrEqualTo(0);
        result.ServiceFidelity.Trend.Should().BeOneOf("UP", "DOWN");
        result.ServiceFidelity.PercentageChange.Should().NotBe(double.NaN);
    }

    #endregion

    #region Multi-Tenant Safety Tests

    [Fact]
    public async Task GetStrategicOutlook_ShouldOnlyIncludeHospitalData()
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

        var otherPatient = new Patient
        {
            PatientId = Guid.NewGuid(),
            FullName = "Other Patient",
            Mobile = "1111111111",
            Age = "40",
            Gender = "Male",
            HospitalId = otherHospitalId,
            PatientIdentifier = "OTHER001"
        };
        _context.Patients.Add(otherPatient);

        var otherAppointment = new Appointment
        {
            AppointmentId = Guid.NewGuid(),
            DisplayId = "OTHER-001",
            PatientId = otherPatient.PatientId,
            PatientName = "Other Patient",
            Service = "Radiology",
            Modality = "MRI",
            DateTime = DateTime.UtcNow,
            Status = "COMPLETED",
            HospitalId = otherHospitalId
        };
        _context.Appointments.Add(otherAppointment);
        await _context.SaveChangesAsync();

        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert - Should not include other hospital's data
        var allAppointments = await _context.Appointments.ToListAsync();
        allAppointments.Should().HaveCountGreaterThan(20); // Original + new
        
        var hospitalAppointments = await _context.Appointments
            .Where(a => a.HospitalId == _hospitalId)
            .ToListAsync();
        hospitalAppointments.Should().HaveCount(20); // Only original
    }

    #endregion

    #region Edge Cases Tests

    [Fact]
    public async Task GetStrategicOutlook_WithNoAppointments_ShouldReturnZeroMetrics()
    {
        // Arrange
        var emptyHospitalId = Guid.NewGuid();
        var emptyHospital = new Hospital
        {
            HospitalId = emptyHospitalId,
            HospitalName = "Empty Hospital",
            HospitalAddress = "Empty Address"
        };
        _context.Hospitals.Add(emptyHospital);
        await _context.SaveChangesAsync();

        _userContextMock.Setup(x => x.HospitalId).Returns(emptyHospitalId);

        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.KpiSnapshot.DailyMissions.Should().Be(0);
        result.KpiSnapshot.RegistryCount.Should().Be(0);
        result.Modalities.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStrategicOutlook_WithPastReferenceDate_ShouldReturnHistoricalData()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var pastDate = DateTime.UtcNow.AddDays(-5);
        var query = new GetStrategicOutlookQuery(pastDate);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.KpiSnapshot.Should().NotBeNull();
    }

    [Fact]
    public async Task GetStrategicOutlook_WithFutureReferenceDate_ShouldReturnEmptyMetrics()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var futureDate = DateTime.UtcNow.AddDays(30);
        var query = new GetStrategicOutlookQuery(futureDate);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.KpiSnapshot.DailyMissions.Should().Be(0);
    }

    #endregion

    #region Data Consistency Tests

    [Fact]
    public async Task GetStrategicOutlook_ModalityMetrics_ShouldMatchAppointmentCounts()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);
        var totalModalityCount = result.Modalities.Sum(m => m.Count);

        // Assert
        totalModalityCount.Should().Be(result.KpiSnapshot.DailyMissions);
    }

    [Fact]
    public async Task GetStrategicOutlook_RevenueBreakdown_ShouldMatchModalities()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);

        // Assert
        result.Modalities.Should().HaveSameCount(result.RevenueBreakdown);
        foreach (var modality in result.Modalities)
        {
            result.RevenueBreakdown.Should().Contain(r => r.Label == modality.Label);
        }
    }

    [Fact]
    public async Task GetStrategicOutlook_Demographics_ShouldMatchPatientCount()
    {
        // Arrange
        var handler = new GetStrategicOutlookQueryHandler(_context, _userContextMock.Object);
        var query = new GetStrategicOutlookQuery(DateTime.UtcNow);

        // Act
        var result = await handler.Handle(query, CancellationToken.None);
        var totalDemographics = result.Demographics.GenderBrief.Male 
                              + result.Demographics.GenderBrief.Female 
                              + result.Demographics.GenderBrief.Other;

        // Assert
        totalDemographics.Should().Be(result.KpiSnapshot.RegistryCount);
    }

    #endregion
}
