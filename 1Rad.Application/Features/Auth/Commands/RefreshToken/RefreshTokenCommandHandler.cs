using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Auth.Commands.TokenRefresh;

public class RefreshTokenCommandHandler : IRequestHandler<RefreshTokenCommand, RefreshTokenResponse>
{
    private readonly IApplicationDbContext _context;
    private readonly IJwtProvider _jwtProvider;
    private readonly ILogger<RefreshTokenCommandHandler> _logger;

    public RefreshTokenCommandHandler(
        IApplicationDbContext context, 
        IJwtProvider jwtProvider, 
        ILogger<RefreshTokenCommandHandler> logger)
    {
        _context = context;
        _jwtProvider = jwtProvider;
        _logger = logger;
    }

    public async Task<RefreshTokenResponse> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var refreshToken = await _context.RefreshTokens
                .Include(t => t.User)
                    .ThenInclude(u => u.HospitalMappings)
                        .ThenInclude(m => m.Hospital)
                .Include(t => t.User)
                    .ThenInclude(u => u.HospitalMappings)
                        .ThenInclude(m => m.Roles)
                .FirstOrDefaultAsync(t => t.Token == request.RefreshToken, cancellationToken);

            if (refreshToken == null || !refreshToken.IsActive)
            {
                return new RefreshTokenResponse { Success = false, Error = "Invalid or expired refresh token." };
            }

            var user = refreshToken.User;
            var activeMapping = user.HospitalMappings.FirstOrDefault(m => m.IsDefault) 
                                ?? user.HospitalMappings.FirstOrDefault();

            if (activeMapping == null)
            {
                return new RefreshTokenResponse { Success = false, Error = "User has no hospital mappings." };
            }

            var authorizedHospitalIds = user.HospitalMappings.Select(m => m.HospitalId).ToList();

            // 1. Generate New Access Token
            var newAccessToken = _jwtProvider.GenerateContextualToken(user, activeMapping, authorizedHospitalIds);

            // 2. Rotate Refresh Token (Tactical security measure)
            var newRefreshTokenString = _jwtProvider.GenerateRefreshToken();
            
            // Revoke old token
            refreshToken.RevokedAt = DateTime.UtcNow;
            refreshToken.ReplacedByToken = newRefreshTokenString;

            // Create new token
            var newRefreshTokenEntity = new RefreshToken
            {
                UserId = user.UserId,
                Token = newRefreshTokenString,
                ExpiresAt = DateTime.UtcNow.AddDays(7)
            };

            _context.RefreshTokens.Add(newRefreshTokenEntity);
            await _context.SaveChangesAsync(cancellationToken);

            return new RefreshTokenResponse
            {
                Success = true,
                AccessToken = newAccessToken,
                RefreshToken = newRefreshTokenString
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical failure during token refresh.");
            return new RefreshTokenResponse { Success = false, Error = "An internal server error occurred." };
        }
    }
}
