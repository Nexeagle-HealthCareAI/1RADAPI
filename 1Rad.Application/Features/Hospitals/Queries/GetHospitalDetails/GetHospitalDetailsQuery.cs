using MediatR;

namespace _1Rad.Application.Features.Hospitals.Queries.GetHospitalDetails;

public record GetHospitalDetailsQuery(Guid HospitalId) : IRequest<HospitalDetailsDto>;

public record ChartDataPointDto(string Label, int Value);

public record MetricGroupDto(
    List<ChartDataPointDto> Daily,
    List<ChartDataPointDto> Weekly,
    List<ChartDataPointDto> Monthly,
    List<ChartDataPointDto> Yearly
);

public record HospitalStatsDto(
    MetricGroupDto UniquePatients,
    MetricGroupDto Appointments
);

public record HospitalUserDto(
    string Name,
    string Role,
    string Contact,
    string Email,
    string Status,
    string? LastLoginTime,
    string? LoginMethod
);

public record DoctorStatsDto(
    int Daily,
    int Weekly,
    int Monthly,
    int Yearly
);

public record HospitalDoctorDto(
    string Name,
    List<string> Departments,
    string Speciality,
    string RegisteredOn,
    string Degree,
    string RegistrationNumber,
    DoctorStatsDto Appointments,
    DoctorStatsDto UniquePatients
);

public record HospitalDetailsDto(
    Guid Id,
    string Name,
    string Address,
    string City,
    string State,
    string ContactNumber,
    string Email,
    string HospitalType,
    string PartnerName,
    int TotalPatients,
    string RegisteredOn,
    string SubscriptionMode,
    string PaymentMode,
    string Status,
    bool IsAutoBillingEnabled,
    List<HospitalUserDto> Users,
    List<HospitalDoctorDto> Doctors,
    HospitalStatsDto Stats
);
