using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Auth.Commands.Login;

public class LoginCommandHandler : IRequestHandler<LoginCommand, LoginResponse>
{
    private readonly IApplicationDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtProvider _jwtProvider;
    private readonly IActiveSessionCache _sessionCache;
    private readonly ISessionAlertService _sessionAlerts;
    private readonly ILogger<LoginCommandHandler> _logger;

    // Whitelist accepted device categories — anything else falls back to
    // UNKNOWN. We accept lowercase too so the frontend doesn't have to worry
    // about casing.
    private static readonly HashSet<string> ValidCategories =
        new(StringComparer.OrdinalIgnoreCase) { "DESKTOP", "MOBILE", "TABLET", "UNKNOWN" };

    public LoginCommandHandler(
        IApplicationDbContext context,
        IPasswordHasher passwordHasher,
        IJwtProvider jwtProvider,
        IActiveSessionCache sessionCache,
        ISessionAlertService sessionAlerts,
        ILogger<LoginCommandHandler> logger)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _jwtProvider = jwtProvider;
        _sessionCache = sessionCache;
        _sessionAlerts = sessionAlerts;
        _logger = logger;
    }

    public async Task<LoginResponse> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Attempting login for identifier: {Identifier}", request.Identifier);

            // 1. Resolve User by Email or Mobile
            var user = await _context.Users
                .Include(u => u.HospitalMappings)
                    .ThenInclude(m => m.Hospital)
                        .ThenInclude(h => h.Group)
                .Include(u => u.HospitalMappings)
                    .ThenInclude(m => m.Roles)
                .Include(u => u.HospitalMappings)
                    .ThenInclude(m => m.CustomRoles)
                .FirstOrDefaultAsync(u => u.Email == request.Identifier || u.Mobile == request.Identifier, cancellationToken);

            if (user == null)
            {
                _logger.LogWarning("Login failed: User not found for {Identifier}", request.Identifier);
                return new LoginResponse { 
                    Success = false, 
                    Error = "This identity is not recognized in the 1Rad grid.",
                    ErrorCode = "USER_NOT_FOUND"
                };
            }

            // 2. Security Check: Status
            if (user.Status != UserStatus.Active)
            {
                _logger.LogWarning("Login blocked: User {UserId} is not Active (Status: {Status})", user.UserId, user.Status);
                return new LoginResponse { 
                    Success = false, 
                    Error = $"Action Required: Your clinical account is currently {user.Status}.",
                    ErrorCode = "ACCOUNT_INACTIVE",
                    AccountStatus = user.Status.ToString()
                };
            }

            // 3. Verify Password
            // Guard: a user may exist with a NULL PasswordHash if account
            // creation got stuck after OTP but before identity-setup.
            // BCrypt.Verify throws ArgumentException on null/empty hash,
            // which used to surface as a confusing "ARGUMENT_INVALID" 400.
            if (string.IsNullOrEmpty(user.PasswordHash))
            {
                _logger.LogWarning("Login failed: User {UserId} has no PasswordHash (incomplete identity setup)", user.UserId);
                return new LoginResponse {
                    Success = false,
                    Error = "Account setup is incomplete. Please finish identity setup before signing in.",
                    ErrorCode = "PASSWORD_NOT_SET"
                };
            }

            if (!_passwordHasher.Verify(request.Password, user.PasswordHash))
            {
                _logger.LogWarning("Login failed: Invalid password for user {UserId}", user.UserId);
                return new LoginResponse {
                    Success = false,
                    Error = "Invalid credentials protocol failed. Verify your secure key.",
                    ErrorCode = "INVALID_CREDENTIALS"
                };
            }

            // 4. Resolve Authority Matrix
            if (!user.HospitalMappings.Any())
            {
                _logger.LogWarning("Login failed: User {UserId} has no associated hospitals", user.UserId);
                return new LoginResponse { Success = false, Error = "Account misconfiguration: No authorized hospitals found." };
            }

            var activeMapping = user.HospitalMappings.FirstOrDefault(m => m.IsDefault) 
                                ?? user.HospitalMappings.First();

            var authorizedHospitalIds = user.HospitalMappings.Select(m => m.HospitalId).ToList();

            // 5. Session policy — one active session per DeviceCategory per
            //    user. A login from the same category revokes the existing
            //    session (forced logout); a login from a NEW category adds
            //    to the active set (still bounded by the 3-category cap).
            var category = (request.DeviceCategory ?? "UNKNOWN").ToUpperInvariant();
            if (!ValidCategories.Contains(category)) category = "UNKNOWN";

            var nowUtc = DateTime.UtcNow;
            var existingForCategory = await _context.RefreshTokens
                .Where(rt => rt.UserId == user.UserId
                          && rt.DeviceCategory == category
                          && rt.RevokedAt == null
                          && rt.ExpiresAt > nowUtc)
                .ToListAsync(cancellationToken);

            foreach (var oldSession in existingForCategory)
            {
                oldSession.RevokedAt = nowUtc;
                oldSession.RevokedByIp = request.IpAddress;
                oldSession.LoggedOutReason = "FORCED_BY_NEW_DEVICE";
                if (oldSession.SessionId.HasValue)
                {
                    _sessionCache.Revoke(oldSession.SessionId.Value);
                }
            }

            var sessionId = Guid.NewGuid();
            var expiresAt = nowUtc.AddDays(7);

            // 6. Generate Contextual JWT (now carrying the sid claim).
            var accessToken = _jwtProvider.GenerateContextualToken(
                user, activeMapping, authorizedHospitalIds, sessionId);

            // 7. Refresh token row records full device context for the
            //    Active Sessions UI + audit.
            var refreshTokenString = _jwtProvider.GenerateRefreshToken();
            var refreshTokenEntity = new RefreshToken
            {
                UserId = user.UserId,
                Token = refreshTokenString,
                ExpiresAt = expiresAt,
                SessionId = sessionId,
                DeviceCategory = category,
                DeviceName = string.IsNullOrWhiteSpace(request.DeviceName) ? null : Trim(request.DeviceName, 100),
                UserAgent = string.IsNullOrWhiteSpace(request.UserAgent) ? null : Trim(request.UserAgent, 512),
                IpAddress = string.IsNullOrWhiteSpace(request.IpAddress) ? null : Trim(request.IpAddress, 45),
                CreatedByIp = request.IpAddress,
                LastSeenAt = nowUtc,
            };

            _context.RefreshTokens.Add(refreshTokenEntity);
            await _context.SaveChangesAsync(cancellationToken);

            // 8. Prime the active-session cache so the very next request
            //    using this token doesn't have to round-trip the DB.
            _sessionCache.MarkActive(sessionId, expiresAt);

            // 9. Send patient/staff a heads-up — covers two cases:
            //    (a) An existing session of this category was forced out
            //        ("Your other Chrome was signed out because someone signed
            //        in here").
            //    (b) This is the first time we've seen this DeviceCategory
            //        for this user ("New sign-in on Mobile").
            //    Best-effort; the fire-and-forget call swallows transport
            //    failures so login latency isn't tied to the SMS gateway.
            var wasForced = existingForCategory.Count > 0;
            var isNewCategory = !wasForced && !await _context.RefreshTokens.AnyAsync(
                rt => rt.UserId == user.UserId
                   && rt.DeviceCategory == category
                   && rt.SessionId != sessionId,
                cancellationToken);
            if (wasForced || isNewCategory)
            {
                _ = _sessionAlerts.NotifyNewSessionAsync(new NewSessionAlert(
                    user.UserId,
                    user.Email,
                    user.Mobile,
                    user.FullName,
                    category,
                    refreshTokenEntity.DeviceName,
                    request.IpAddress,
                    nowUtc,
                    wasForced));
            }
            
            // 7. Construct Response
            return new LoginResponse
            {
                Success = true,
                AccessToken = accessToken,
                RefreshToken = refreshTokenString,
                UserProfile = new UserProfileDto
                {
                    UserId = user.UserId,
                    FullName = user.FullName,
                    Email = user.Email,
                    AuthorizedHospitals = user.HospitalMappings.Select(m => new AuthorizedHospitalDto
                    {
                        HospitalId = m.HospitalId,
                        HospitalName = m.Hospital?.HospitalName ?? "Unknown Hub",
                        GroupName = m.Hospital?.Group?.GroupName ?? string.Empty,
                        RoleName = string.Join(", ", m.Roles.Select(r => r.RoleName).Concat(m.CustomRoles.Select(cr => cr.RoleName)).DefaultIfEmpty("User")),
                        IsDefault = m.IsDefault
                    }).ToList()
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical failure during login for {Identifier}", request.Identifier);
            return new LoginResponse { Success = false, Error = "An internal server error occurred." };
        }
    }

    private static string? Trim(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max));
}
