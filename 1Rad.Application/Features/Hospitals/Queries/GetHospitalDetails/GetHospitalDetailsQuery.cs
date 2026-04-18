using MediatR;

namespace _1Rad.Application.Features.Hospitals.Queries.GetHospitalDetails;

public record GetHospitalDetailsQuery(Guid HospitalId) : IRequest<HospitalDetailsDto>;

public record HospitalDetailsDto(
    Guid HospitalId,
    string HospitalName,
    string HospitalAddress,
    string? GSTIN,
    string? RegistrationNumber,
    string? PAN,
    string? NABHNumber,
    string Status);
