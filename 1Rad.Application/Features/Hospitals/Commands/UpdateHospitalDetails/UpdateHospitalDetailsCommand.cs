using MediatR;

namespace _1Rad.Application.Features.Hospitals.Commands.UpdateHospitalDetails;

public record UpdateHospitalDetailsCommand(
    Guid HospitalId,
    string HospitalName,
    string HospitalAddress,
    string? GSTIN,
    string? RegistrationNumber,
    string? PAN,
    string? NABHNumber,
    bool IsAutoBillingEnabled,
    decimal? Latitude = null,
    decimal? Longitude = null,
    // Review R9: null = leave as is; "" clears.
    string? PublicContact = null,
    string? PublicEmail = null) : IRequest<(bool Success, string? Error)>;
