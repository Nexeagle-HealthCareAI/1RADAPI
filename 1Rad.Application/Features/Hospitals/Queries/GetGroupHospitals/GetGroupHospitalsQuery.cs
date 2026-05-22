using MediatR;
using System;
using System.Collections.Generic;

namespace _1Rad.Application.Features.Hospitals.Queries.GetGroupHospitals;

public record GroupHospitalDto(
    Guid HospitalId,
    string HospitalName,
    string HospitalAddress,
    string? GSTIN,
    string? RegistrationNumber,
    string? PAN,
    string? NABHNumber,
    string Status,
    bool IsAutoBillingEnabled
);

public record GetGroupHospitalsQuery() : IRequest<List<GroupHospitalDto>>;
