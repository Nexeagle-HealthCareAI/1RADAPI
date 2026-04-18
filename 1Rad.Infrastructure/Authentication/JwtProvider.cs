using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using _1Rad.Application.Interfaces;
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

        return CreateToken(claims, 15); // 15 minutes as requested
    }

    public string GenerateAccessToken(Guid userId, string mobile, string email, string role)
    {
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim("mobile", mobile),
            new Claim(ClaimTypes.Role, role),
            new Claim("type", "access")
        };

        return CreateToken(claims, 60 * 24); // 24 hours for access token
    }

    private string CreateToken(IEnumerable<Claim> claims, int expiryMinutes)
    {
        var secretKey = _configuration["Jwt:Secret"] ?? "a_very_long_and_secure_secret_key_for_1rad_api_development";
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
