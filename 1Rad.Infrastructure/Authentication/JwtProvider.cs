using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace _1Rad.Infrastructure.Authentication;

public class JwtProvider : IJwtProvider
{
    private readonly IConfiguration _configuration;

    public JwtProvider(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string GenerateInitiationToken(string mobile, Guid? userId = null)
    {
        var claims = new List<Claim>
        {
            new Claim("mobile", mobile),
            new Claim("type", "initiation")
        };

        if (userId.HasValue)
        {
            claims.Add(new Claim("userId", userId.Value.ToString()));
        }

        // 60 minutes for the registration flow. The window starts at OTP-verify
        // and must cover the WHOLE multi-step form (details → clinical → centre
        // legal details → plan), since identity-setup (which consumes the token)
        // is the final step. 15 min was too tight for a first-time user entering
        // GSTIN/PAN/address and comparing plans → "verification expired" errors.
        // A pre-account token scoped to one OTP-verified mobile is low-risk at 60.
        return CreateToken(claims, 60);
    }

    public string GenerateContextualToken(User user, UserHospitalMapping activeMapping, IEnumerable<Guid> authorizedHospitalIds, Guid? sessionId = null)
    {
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.UserId.ToString()),
            new Claim(JwtRegisteredClaimNames.Name, user.FullName),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("mobile", user.Mobile),
            new Claim("type", "access"),

            // Tactical Context Claims
            new Claim("cid", activeMapping.HospitalId.ToString()), // Current Hospital ID
            new Claim("rid", string.Join(",", activeMapping.Roles.Select(r => r.RoleId.ToString()).Concat(activeMapping.CustomRoles?.Select(cr => cr.CustomRoleId.ToString()) ?? Enumerable.Empty<string>()))), // Comma-separated Role IDs
            new Claim("gid", activeMapping.Hospital?.GroupId?.ToString() ?? string.Empty), // Group / Chain ID
            new Claim("hubs", string.Join(",", authorizedHospitalIds)) // Comma-separated list of authorized Hubs
        };

        // Session id — the session validation middleware uses this to check
        // against the active-session cache on every authenticated request.
        // Optional so legacy callers can still mint tokens; those tokens fail
        // the middleware and the client is forced to re-login.
        if (sessionId.HasValue)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Sid, sessionId.Value.ToString()));
        }

        // ASP.NET Identity Compatibility: Add multiple role claims
        foreach (var role in activeMapping.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role.RoleName));
        }

        if (activeMapping.CustomRoles != null)
        {
            foreach (var customRole in activeMapping.CustomRoles)
            {
                claims.Add(new Claim(ClaimTypes.Role, customRole.RoleName));
            }
        }

        if (!activeMapping.Roles.Any() && (activeMapping.CustomRoles == null || !activeMapping.CustomRoles.Any()))
        {
            claims.Add(new Claim(ClaimTypes.Role, "User"));
        }

        return CreateToken(claims, 60 * 24); // 24 hours for access token
    }

    public string GenerateRefreshToken()
    {
        var randomNumber = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomNumber);
        return Convert.ToBase64String(randomNumber);
    }

    public string GenerateResetToken(Guid userId)
    {
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim("type", "password-reset")
        };

        return CreateToken(claims, 5); // 5 minutes for security finalization
    }

    public ClaimsPrincipal? ValidateToken(string token, string? expectedType = null)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var secretKey = _configuration["Jwt:Secret"] ?? "a_very_long_and_secure_secret_key_for_1rad_api_development_2026";
            var key = Encoding.UTF8.GetBytes(secretKey);

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = true,
                ValidIssuer = _configuration["Jwt:Issuer"] ?? "1RadAPI",
                ValidateAudience = true,
                ValidAudience = _configuration["Jwt:Audience"] ?? "1RadClient",
                ClockSkew = TimeSpan.Zero
            };

            var principal = handler.ValidateToken(token, validationParameters, out SecurityToken validatedToken);
            
            // If expectedType is specified, validate the token type
            if (!string.IsNullOrEmpty(expectedType))
            {
                var typeClaim = principal.FindFirst("type")?.Value;
                if (typeClaim != expectedType)
                {
                    return null;
                }
            }

            return principal;
        }
        catch (SecurityTokenExpiredException)
        {
            // Token has expired
            return null;
        }
        catch (Exception)
        {
            // Token is invalid
            return null;
        }
    }

    private string CreateToken(IEnumerable<Claim> claims, int expiryMinutes)
    {
        var secretKey = _configuration["Jwt:Secret"] ?? "a_very_long_and_secure_secret_key_for_1rad_api_development_2026";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"] ?? "1RadAPI",
            audience: _configuration["Jwt:Audience"] ?? "1RadClient",
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expiryMinutes),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
