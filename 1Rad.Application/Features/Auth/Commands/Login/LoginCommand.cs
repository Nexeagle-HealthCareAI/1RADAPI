using MediatR;

namespace _1Rad.Application.Features.Auth.Commands.Login;

// Device fields are optional — pre-session-management clients omit them and
// the handler degrades to DeviceCategory=UNKNOWN. The session validation
// middleware treats UNKNOWN like any other category for the cap.
public record LoginCommand(
    string Identifier,
    string Password,
    string? DeviceCategory = null,
    string? DeviceName = null,
    string? UserAgent = null,
    string? IpAddress = null
) : IRequest<LoginResponse>;

public class LoginResponse
{
    public bool Success { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; } // USER_NOT_FOUND, ACCOUNT_INACTIVE, INVALID_CREDENTIALS
    public string? AccountStatus { get; set; }
    public UserProfileDto? UserProfile { get; set; }
}

public class UserProfileDto
{
    public Guid UserId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public List<AuthorizedHospitalDto> AuthorizedHospitals { get; set; } = new();
}

public class AuthorizedHospitalDto
{
    public Guid HospitalId { get; set; }
    public string HospitalName { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
}
