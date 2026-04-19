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
    private readonly ILogger<LoginCommandHandler> _logger;

    public LoginCommandHandler(
        IApplicationDbContext context, 
        IPasswordHasher passwordHasher, 
        IJwtProvider jwtProvider, 
        ILogger<LoginCommandHandler> logger)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _jwtProvider = jwtProvider;
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
                .Include(u => u.HospitalMappings)
                    .ThenInclude(m => m.Roles)
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

            // 5. Generate Contextual JWT
            var accessToken = _jwtProvider.GenerateContextualToken(user, activeMapping, authorizedHospitalIds);

            // 6. Manage Refresh Token
            var refreshTokenString = _jwtProvider.GenerateRefreshToken();
            var refreshTokenEntity = new RefreshToken
            {
                UserId = user.UserId,
                Token = refreshTokenString,
                ExpiresAt = DateTime.UtcNow.AddDays(7), // 7 days session
            };

            _context.RefreshTokens.Add(refreshTokenEntity);
            await _context.SaveChangesAsync(cancellationToken);
            
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
                        RoleName = string.Join(", ", m.Roles.Select(r => r.RoleName).DefaultIfEmpty("User")),
                        IsDefault = m.IsDefault
                    }).ToList()
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical failure during login for {Identifier}", request.Identifier);
            return new LoginResponse { Success = false, Error = $"Internal Error: {ex.Message} | {ex.InnerException?.Message}" };
        }
    }
}
