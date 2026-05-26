using MediatR;

namespace _1Rad.Application.Features.Users.Commands.UpdateClinicalCredentials;

/// <summary>
/// Updates a user's clinical credentials (specialization, degree, license).
/// Scope is intentionally narrow — does NOT touch email/mobile/password,
/// so this endpoint is safe to expose to hospital admins managing their
/// primary admin user from the Hospital Management screen.
/// </summary>
public record UpdateClinicalCredentialsCommand(
    Guid UserId,
    string? Specialization,
    string? Degree,
    string? LicenseNo
) : IRequest<UpdateClinicalCredentialsResponse>;

public record UpdateClinicalCredentialsResponse(
    bool Success,
    string? Error = null,
    string? ErrorCode = null
);
