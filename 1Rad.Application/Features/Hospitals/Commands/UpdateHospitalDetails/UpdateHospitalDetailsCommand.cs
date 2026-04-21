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
    bool IsAutoBillingEnabled) : IRequest<(bool Success, string? Error)>;
