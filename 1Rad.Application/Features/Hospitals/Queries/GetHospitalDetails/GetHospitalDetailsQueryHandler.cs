using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace _1Rad.Application.Features.Hospitals.Queries.GetHospitalDetails;

public class GetHospitalDetailsQueryHandler : IRequestHandler<GetHospitalDetailsQuery, HospitalDetailsDto>
{
    private readonly IApplicationDbContext _context;

    public GetHospitalDetailsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<HospitalDetailsDto> Handle(GetHospitalDetailsQuery request, CancellationToken cancellationToken)
    {
        var hospital = await _context.Hospitals
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.HospitalId == request.HospitalId, cancellationToken);

        if (hospital == null) return null!;

        // Parse City and State from Address (naive approach for legacy data)
        var addressParts = hospital.HospitalAddress?.Split(',') ?? new string[0];
        var state = addressParts.Length > 0 ? addressParts.Last().Trim() : "Unknown";
        var city = addressParts.Length > 1 ? addressParts[addressParts.Length - 2].Trim() : "Unknown";

        // Fetch Users mapped to this hospital
        var userMappings = await _context.UserHospitalMappings
            .Include(m => m.User)
            .Include(m => m.Roles)
            .Where(m => m.HospitalId == request.HospitalId)
            .ToListAsync(cancellationToken);

        var usersDto = userMappings.Select(m => new HospitalUserDto(
            m.User.FullName,
            m.Roles.FirstOrDefault()?.RoleName ?? "Staff",
            m.User.Mobile ?? "N/A",
            m.User.Email ?? "N/A",
            m.User.Status.ToString(),
            null, // LastLoginTime (not in entity)
            "Email"
        )).ToList();

        // Fetch Doctors from StaffMembers
        var doctors = await _context.StaffMembers
            .Where(s => s.HospitalId == request.HospitalId && (s.Designation == "Doctor" || s.Specialization != null))
            .ToListAsync(cancellationToken);

        var doctorsDto = doctors.Select(d => new HospitalDoctorDto(
            d.FullName,
            new List<string> { d.Department ?? "General" },
            d.Specialization ?? "General",
            d.JoiningDate?.ToString("yyyy-MM-dd") ?? d.CreatedAt.ToString("yyyy-MM-dd"),
            d.Degree ?? "MBBS",
            d.LicenseNo ?? "N/A",
            new DoctorStatsDto(Random.Shared.Next(5, 20), Random.Shared.Next(30, 100), Random.Shared.Next(150, 400), Random.Shared.Next(1000, 4000)),
            new DoctorStatsDto(Random.Shared.Next(5, 20), Random.Shared.Next(30, 100), Random.Shared.Next(150, 400), Random.Shared.Next(1000, 4000))
        )).ToList();

        // Active Subscription
        var sub = await _context.HospitalSubscriptions
            .Include(s => s.Plan)
            .Where(s => s.HospitalId == request.HospitalId && s.Status == "Active")
            .FirstOrDefaultAsync(cancellationToken);

        // Fetch actual patients to get a true total count
        var totalPatients = await _context.Patients
            .Where(p => p.HospitalId == request.HospitalId)
            .CountAsync(cancellationToken);

        // Generate mock analytics based on total patients
        var basePatients = totalPatients > 0 ? totalPatients / 30 : 50; // Daily average baseline
        
        var statsDto = new HospitalStatsDto(
            UniquePatients: GenerateMetricGroup(basePatients, 0.1),
            Appointments: GenerateMetricGroup(basePatients * 2, 0.15)
        );

        // Pick the primary admin — first user whose role contains "Admin"
        // (covers AdminDoctor, AdminOperator, Admin, etc.). If none found we
        // fall back to the first user attached to this hospital so the UI
        // still has someone to render in the admin slot.
        var adminMapping =
            userMappings.FirstOrDefault(m => m.Roles.Any(r => r.RoleName != null && r.RoleName.Contains("Admin", StringComparison.OrdinalIgnoreCase)))
            ?? userMappings.FirstOrDefault();

        HospitalAdminDto? adminDto = null;
        if (adminMapping != null && adminMapping.User != null)
        {
            adminDto = new HospitalAdminDto(
                UserId:         adminMapping.User.UserId,
                FullName:       adminMapping.User.FullName ?? "Unknown",
                Email:          adminMapping.User.Email ?? "N/A",
                Mobile:         adminMapping.User.Mobile ?? "N/A",
                Role:           adminMapping.Roles.FirstOrDefault()?.RoleName ?? "Staff",
                Status:         adminMapping.User.Status.ToString(),
                RegisteredOn:   adminMapping.User.CreatedAt.ToString("yyyy-MM-dd"),
                Specialization: adminMapping.User.Specialization,
                Degree:         adminMapping.User.Degree,
                LicenseNo:      adminMapping.User.LicenseNo
            );
        }

        return new HospitalDetailsDto(
            hospital.HospitalId,
            hospital.HospitalName ?? "Unknown Clinic",
            hospital.HospitalAddress ?? "N/A",
            city,
            state,
            "N/A", // ContactNumber
            "N/A", // Email
            "Diagnostic Center", // HospitalType
            "Nexeagle Direct", // PartnerName
            totalPatients,
            hospital.CreatedAt.ToString("yyyy-MM-dd"),
            sub?.Plan?.Name ?? "Trial",
            sub?.BillingCycle ?? "None",
            hospital.Status,
            hospital.IsAutoBillingEnabled,
            // Explicit hospital fields — match entity property names so the
            // frontend can bind without aliasing
            hospital.HospitalName ?? "Unknown Clinic",
            hospital.HospitalAddress ?? "N/A",
            hospital.GSTIN,
            hospital.RegistrationNumber,
            hospital.PAN,
            hospital.NABHNumber,
            adminDto,
            usersDto,
            doctorsDto,
            statsDto
        );
    }

    private MetricGroupDto GenerateMetricGroup(int baseValue, double volatility)
    {
        var random = new Random();
        int ApplyVolatility(int val) => (int)(val * (1 + (random.NextDouble() * 2 - 1) * volatility));

        var daily = new List<ChartDataPointDto>();
        for (int i = 6; i >= 0; i--)
            daily.Add(new ChartDataPointDto(DateTime.Now.AddDays(-i).ToString("ddd"), ApplyVolatility(baseValue)));

        var weekly = new List<ChartDataPointDto>();
        for (int i = 3; i >= 0; i--)
            weekly.Add(new ChartDataPointDto($"Week {4 - i}", ApplyVolatility(baseValue * 6)));

        var monthly = new List<ChartDataPointDto>();
        for (int i = 5; i >= 0; i--)
            monthly.Add(new ChartDataPointDto(DateTime.Now.AddMonths(-i).ToString("MMM"), ApplyVolatility(baseValue * 25)));

        var yearly = new List<ChartDataPointDto>();
        for (int i = 4; i >= 0; i--)
            yearly.Add(new ChartDataPointDto(DateTime.Now.AddYears(-i).ToString("yyyy"), ApplyVolatility(baseValue * 300)));

        return new MetricGroupDto(daily, weekly, monthly, yearly);
    }
}
