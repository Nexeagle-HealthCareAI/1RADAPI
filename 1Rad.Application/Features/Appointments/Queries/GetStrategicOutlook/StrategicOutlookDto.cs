using System;
using System.Collections.Generic;

namespace _1Rad.Application.Features.Appointments.Queries.GetStrategicOutlook;

public record StrategicOutlookDto(
    KpiSnapshot Kpis,
    List<ModalityMetric> Modalities,
    List<VolumeDataPoint> VolumeTrends,
    DemographicSnapshot Demographics,
    List<SourceMetric> TopSources
);

public record KpiSnapshot(
    int UniversalRegistry,
    int DailyMissions,
    decimal FinancialYield,
    int AverageLatencyMinutes,
    double GrowthPercentage
);

public record ModalityMetric(
    string Label,
    int Count,
    string Color
);

public record VolumeDataPoint(
    string Day,
    int Count,
    bool IsPeak
);

public record DemographicSnapshot(
    GenderBrief Gender,
    List<AgeTier> AgeGroups
);

public record GenderBrief(
    int Male,
    int Female,
    int Other
);

public record AgeTier(
    string Label,
    int Count,
    double Percentage,
    string Color
);

public record SourceMetric(
    string Name,
    int Count
);
