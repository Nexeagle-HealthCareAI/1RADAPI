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
    // ── Explicit hospital fields (match Hospital entity property names so
    //    the frontend's hospitalData state can bind 1:1 with API response). ──
    string HospitalName,
    string HospitalAddress,
    string? GSTIN,
    string? RegistrationNumber,
    string? PAN,
    string? NABHNumber,
    // ── Primary admin user (first user with an Admin role). Null if the
    //    hospital has no admin mapping yet. ──
    HospitalAdminDto? Admin,
    List<HospitalUserDto> Users,
    List<HospitalDoctorDto> Doctors,
    HospitalStatsDto Stats
);

/// <summary>
/// Rich admin profile for the hospital edit screen — name, contact details,
/// role, and account status. Different from HospitalUserDto (which is the
/// terse users-list row).
/// </summary>
public record HospitalAdminDto(
    Guid UserId,
    string FullName,
    string Email,
    string Mobile,
    string Role,
    string Status,
    string? RegisteredOn,
    // Clinical credentials captured during doctor registration. Editable
    // from the Hospital Management screen via /users/{id}/clinical-credentials.
    string? Specialization,
    string? Degree,
    string? LicenseNo
);
