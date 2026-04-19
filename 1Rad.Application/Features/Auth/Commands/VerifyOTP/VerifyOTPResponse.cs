using _1Rad.Domain.Entities;

namespace _1Rad.Application.Features.Auth.Commands.VerifyOTP;

public record VerifyOTPResponse(
    bool Success, 
    string? Token = null, 
    string? RefreshToken = null, 
    string? Message = null,
    bool IsRegistered = false,
    UserDetailsDto? User = null);

public record UserDetailsDto(
    Guid UserId, 
    string FullName, 
    string Email, 
    string Mobile, 
    string RoleName,
    List<AuthorizedHospitalDto> AuthorizedHospitals = null!);

public record AuthorizedHospitalDto(
    Guid HospitalId, 
    string HospitalName, 
    string RoleName, 
    bool IsDefault);
