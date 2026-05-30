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
    private readonly IActiveSessionCache _sessionCache;
    private readonly ILogger<RefreshTokenCommandHandler> _logger;

    public RefreshTokenCommandHandler(
        IApplicationDbContext context,
        IJwtProvider jwtProvider,
        IActiveSessionCache sessionCache,
        ILogger<RefreshTokenCommandHandler> logger)
    {
        _context = context;
        _jwtProvider = jwtProvider;
        _sessionCache = sessionCache;
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
                .Include(t => t.User)
                    .ThenInclude(u => u.HospitalMappings)
                        .ThenInclude(m => m.CustomRoles)
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

            // Refresh PRESERVES the original session id. A "session" is the
            // user's continuous presence on a single device — refresh-token
            // rotation is just a token-hygiene step, not the start of a new
            // session. Carrying the sid forward keeps Active Sessions stable
            // and the cache entry valid across refreshes.
            var sessionId = refreshToken.SessionId ?? Guid.NewGuid();

            // 1. Generate New Access Token (with the same sid as before).
            var newAccessToken = _jwtProvider.GenerateContextualToken(
                user, activeMapping, authorizedHospitalIds, sessionId);

            // 2. Rotate Refresh Token (Tactical security measure).
            var newRefreshTokenString = _jwtProvider.GenerateRefreshToken();
            var nowUtc = DateTime.UtcNow;
            var newExpiry = nowUtc.AddDays(7);

            // Revoke old token row — important: the session is NOT being
            // revoked, only this row in the rotation chain. The cache still
            // points at sessionId; we just re-prime it with the new expiry
            // below.
            refreshToken.RevokedAt = nowUtc;
            refreshToken.ReplacedByToken = newRefreshTokenString;

            // Create new token — carrying forward session + device metadata.
            var newRefreshTokenEntity = new RefreshToken
            {
                UserId = user.UserId,
                Token = newRefreshTokenString,
                ExpiresAt = newExpiry,
                SessionId = sessionId,
                DeviceCategory = refreshToken.DeviceCategory,
                DeviceName = refreshToken.DeviceName,
                UserAgent = refreshToken.UserAgent,
                IpAddress = refreshToken.IpAddress,
                CreatedByIp = refreshToken.IpAddress,
                LastSeenAt = nowUtc,
            };

            _context.RefreshTokens.Add(newRefreshTokenEntity);
            await _context.SaveChangesAsync(cancellationToken);

            // Re-prime the cache with the new expiry so the session continues
            // unbroken — without this, the cache entry would expire on the
            // OLD row's window and the next request would unnecessarily fall
            // back to a DB lookup.
            _sessionCache.MarkActive(sessionId, newExpiry);

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
