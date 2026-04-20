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

    public GetStrategicOutlookQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<StrategicOutlookDto> Handle(GetStrategicOutlookQuery request, CancellationToken cancellationToken)
    {
        var today = request.ReferenceDate ?? DateTime.Today;
        var startOfWeek = today.AddDays(-6);

        // 1. KPI Snapshot
        var universalRegistryCount = await _context.Patients.CountAsync(cancellationToken);
        
        var dailyAppointments = await _context.Appointments
            .Where(a => a.DateTime.Date == today.Date)
            .ToListAsync(cancellationToken);
            
        var dailyMissions = dailyAppointments.Count;
        
        // Mock financial yield for now ($85 per mission)
        var financialYield = dailyMissions * 85m;
        
        // Mock latency (average 38-45 mins)
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
            .Where(a => a.DateTime.Date == today.Date)
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
        var weekData = await _context.Appointments
            .Where(a => a.DateTime.Date >= startOfWeek.Date && a.DateTime.Date <= today.Date)
            .GroupBy(a => a.DateTime.Date)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var trend = new List<VolumeDataPoint>();
        for (int i = 0; i < 7; i++)
        {
            var date = startOfWeek.AddDays(i);
            var dayData = weekData.FirstOrDefault(w => w.Day == date.Date);
            trend.Add(new VolumeDataPoint(
                date.ToString("ddd").ToUpper(),
                dayData?.Count ?? 0,
                (dayData?.Count ?? 0) > 100 // Peak threshold
            ));
        }

        // 4. Demographic Snapshot
        var patients = await _context.Patients.ToListAsync(cancellationToken);
        
        var genderBrief = new GenderBrief(
            patients.Count(p => p.Gender?.ToUpper() == "MALE"),
            patients.Count(p => p.Gender?.ToUpper() == "FEMALE"),
            patients.Count(p => p.Gender?.ToUpper() != "MALE" && p.Gender?.ToUpper() != "FEMALE")
        );

        var ageTiers = new List<AgeTier>();
        var total = patients.Count > 0 ? patients.Count : 1;
        
        var tiers = new[] {
            new { Label = "0-18 (Paediatric)", Min = 0, Max = 18, Color = "#00cec9" },
            new { Label = "19-45 (Adult)", Min = 19, Max = 45, Color = "#0f52ba" },
            new { Label = "46-65 (Mature)", Min = 46, Max = 65, Color = "#f39c12" },
            new { Label = "66+ (Geriatric)", Min = 66, Max = 150, Color = "#d63031" }
        };

        foreach (var tier in tiers)
        {
            var count = patients.Count(p => {
                if (int.TryParse(p.Age, out int age))
                {
                    return age >= tier.Min && age <= tier.Max;
                }
                return false;
            });
            ageTiers.Add(new AgeTier(tier.Label, count, (double)count / total * 100, tier.Color));
        }

        // 5. Top Sources
        var topSources = await _context.Appointments
            .Where(a => !string.IsNullOrEmpty(a.ReferredBy))
            .GroupBy(a => a.ReferredBy)
            .Select(g => new SourceMetric(g.Key, g.Count()))
            .OrderByDescending(s => s.Count)
            .Take(5)
            .ToListAsync(cancellationToken);

        return new StrategicOutlookDto(kpis, modalities, trend, new DemographicSnapshot(genderBrief, ageTiers), topSources);
    }
}
