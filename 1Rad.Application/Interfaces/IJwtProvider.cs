using _1Rad.Domain.Entities;
using System.Security.Claims;

namespace _1Rad.Application.Interfaces;

public interface IJwtProvider
{
    string GenerateInitiationToken(string mobile, Guid? userId = null);
    
    /// <summary>
    /// Generates a standard access token for a specific center context.
    /// </summary>
    string GenerateContextualToken(User user, UserHospitalMapping activeMapping, IEnumerable<Guid> authorizedHospitalIds);
    
    /// <summary>
    /// Generates a cryptographically secure refresh token.
    /// </summary>
    string GenerateRefreshToken();

    /// <summary>
    /// Generates a short-lived reset token for password recovery.
    /// </summary>
    string GenerateResetToken(Guid userId);

    /// <summary>
    /// Validates a JWT token and returns the claims principal.
    /// </summary>
    /// <param name="token">The JWT token to validate</param>
    /// <param name="expectedType">The expected token type (e.g., "password-reset", "initiation", "access")</param>
    /// <returns>Claims principal if valid, null if invalid</returns>
    ClaimsPrincipal? ValidateToken(string token, string? expectedType = null);
}
