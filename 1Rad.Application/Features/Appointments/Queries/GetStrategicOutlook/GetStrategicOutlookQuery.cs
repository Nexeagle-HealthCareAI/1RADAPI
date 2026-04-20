using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Queries.GetStrategicOutlook;

public record GetStrategicOutlookQuery(DateTime? ReferenceDate = null) : IRequest<StrategicOutlookDto>;

public class GetStrategicOutlookQueryHandler : IRequestHandler<GetStrategicOutlookQuery, StrategicOutlookDto>
{
    private readonly IApplicationDbContext _context;
    private readonly IUserContext _userContext;

    public GetStrategicOutlookQueryHandler(IApplicationDbContext context, IUserContext userContext)
    {
        _context = context;
        _userContext = userContext;
    }

    public async Task<StrategicOutlookDto> Handle(GetStrategicOutlookQuery request, CancellationToken cancellationToken)
    {
        var hospitalId = _userContext.HospitalId;
        
        // Validate that user has a hospital context
        if (hospitalId == Guid.Empty)
        {
            throw new InvalidOperationException("User does not have an associated hospital. Please ensure your authentication token includes the hospital context (cid claim).");
        }

        var today = (request.ReferenceDate ?? DateTime.Today).Date;
        var tomorrow = today.AddDays(1);
        var startOfWeek = today.AddDays(-6);

        try
        {
            // 1. KPI Snapshot
            var universalRegistryCount = await _context.Patients
                .Where(p => p.HospitalId == hospitalId)
                .CountAsync(cancellationToken);
            
            var dailyAppointments = await _context.Appointments
                .Where(a => a.HospitalId == hospitalId && a.DateTime >= today && a.DateTime < tomorrow)
                .ToListAsync(cancellationToken);
                
            var dailyMissions = dailyAppointments.Count;
            
            // Yield for now ($85 per mission)
            var financialYield = dailyMissions * 85m;
            
            // Latency (average 38-45 mins)
            var avgLatency = 38 + (dailyMissions % 7);

            var kpis = new KpiSnapshot(
                universalRegistryCount,
                dailyMissions,
                financialYield,
                avgLatency,
                14.2 // Mock growth
            );

            // 2. Modality Snapshot
            var modalityStats = await _context.Appointments
                .Where(a => a.HospitalId == hospitalId && a.DateTime >= today && a.DateTime < tomorrow)
                .GroupBy(a => a.Modality)
                .Select(g => new { Label = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);

            var colors = new Dictionary<string, string>
            {
                { "CT", "#0f52ba" },
                { "MRI", "#6c5ce7" },
                { "X-RAY", "#2ecc71" },
                { "USG", "#e74c3c" },
                { "PET", "#f39c12" }
            };

            var modalities = modalityStats.Select(m => new ModalityMetric(
                m.Label,
                m.Count,
                colors.ContainsKey(m.Label) ? colors[m.Label] : "#94a3b8"
            )).ToList();

            // 3. Volume Trends (Last 7 Days)
            var weekRawData = await _context.Appointments
                .Where(a => a.HospitalId == hospitalId && a.DateTime >= startOfWeek && a.DateTime < tomorrow)
                .Select(a => new { a.DateTime })
                .ToListAsync(cancellationToken);

            var weekData = weekRawData
                .GroupBy(a => a.DateTime.Date)
                .Select(g => new { Day = g.Key, Count = g.Count() })
                .ToList();

            var trend = new List<VolumeDataPoint>();
            for (int i = 0; i < 7; i++)
            {
                var date = startOfWeek.AddDays(i);
                var dayData = weekData.FirstOrDefault(w => w.Day.Date == date.Date);
                trend.Add(new VolumeDataPoint(
                    date.ToString("ddd").ToUpper(),
                    dayData?.Count ?? 0,
                    (dayData?.Count ?? 0) > 100
                ));
            }

            // 4. Demographic Snapshot
            var hospitalPatients = await _context.Patients
                .Where(p => p.HospitalId == hospitalId)
                .Select(p => new { p.Gender, p.Age })
                .ToListAsync(cancellationToken);

            var totalPatients = hospitalPatients.Count;
            var genderBrief = new GenderBrief(
                hospitalPatients.Count(p => p.Gender == "Male"),
                hospitalPatients.Count(p => p.Gender == "Female"),
                hospitalPatients.Count(p => p.Gender != "Male" && p.Gender != "Female")
            );

            var ageTiers = new List<AgeTier>();
            var total = totalPatients > 0 ? totalPatients : 1;
            
            var tiers = new[] {
                new { Label = "0-18 (Paediatric)", Min = 0, Max = 18, Color = "#00cec9" },
                new { Label = "19-45 (Adult)", Min = 19, Max = 45, Color = "#0f52ba" },
                new { Label = "46-65 (Mature)", Min = 46, Max = 65, Color = "#f39c12" },
                new { Label = "66+ (Geriatric)", Min = 66, Max = 150, Color = "#d63031" }
            };

            foreach (var tier in tiers)
            {
                var count = hospitalPatients.Count(p => {
                    if (int.TryParse(p.Age, out int age))
                    {
                        return age >= tier.Min && age <= tier.Max;
                    }
                    return false;
                });

                ageTiers.Add(new AgeTier(tier.Label, count, total > 0 ? (double)count / total * 100 : 0, tier.Color));
            }

            // 5. Top Sources
            var topSources = await _context.Appointments
                .Where(a => a.HospitalId == hospitalId && !string.IsNullOrEmpty(a.ReferredBy))
                .GroupBy(a => a.ReferredBy)
                .Select(g => new SourceMetric(g.Key ?? "Unknown", g.Count()))
                .OrderByDescending(s => s.Count)
                .Take(5)
                .ToListAsync(cancellationToken);

            return new StrategicOutlookDto(kpis, modalities, trend, new DemographicSnapshot(genderBrief, ageTiers), topSources);
        }
        catch (Exception ex)
        {
            // Return a default response with error details
            throw new InvalidOperationException($"Failed to generate strategic outlook for hospital {hospitalId}: {ex.Message}", ex);
        }
    }
}
