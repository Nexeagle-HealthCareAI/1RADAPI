using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Auth.Commands.VerifyOTP;

public class VerifyOTPCommandHandler : IRequestHandler<VerifyOTPCommand, VerifyOTPResponse>
{
    private readonly IApplicationDbContext _context;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtProvider _jwtProvider;
    private readonly ILogger<VerifyOTPCommandHandler> _logger;

    public VerifyOTPCommandHandler(IApplicationDbContext context, IPasswordHasher hasher, IJwtProvider jwtProvider, ILogger<VerifyOTPCommandHandler> logger)
    {
        _context = context;
        _hasher = hasher;
        _jwtProvider = jwtProvider;
        _logger = logger;
    }

    public async Task<VerifyOTPResponse> Handle(VerifyOTPCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Verifying OTP for mobile: {Mobile}", request.Mobile);

        try
        {
            var verification = await _context.OTPVerifications
                .Where(x => x.Identifier == request.Mobile && !x.IsUsed && x.ExpiresAt > DateTime.UtcNow)
                .OrderByDescending(x => x.ExpiresAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (verification == null)
            {
                _logger.LogWarning("No active OTP found for mobile: {Mobile}", request.Mobile);
                return new VerifyOTPResponse(false, Message: "The code provided is invalid or has expired.");
            }

            if (!_hasher.Verify(request.Code, verification.CodeHash))
            {
                _logger.LogWarning("Invalid OTP code provided for mobile: {Mobile}", request.Mobile);
                return new VerifyOTPResponse(false, Message: "Invalid verification code.");
            }

            verification.IsUsed = true;
            await _context.SaveChangesAsync(cancellationToken);

            // Dual-Path Logic: Login or Registration?
            var user = await _context.Users
                .Include(u => u.HospitalMappings)
                    .ThenInclude(m => m.Hospital)
                .Include(u => u.HospitalMappings)
                    .ThenInclude(m => m.Roles)
                .FirstOrDefaultAsync(u => u.Mobile == request.Mobile, cancellationToken);

            if (user != null && user.Status == _1Rad.Domain.Enums.UserStatus.Active)
            {
                _logger.LogInformation("Existing user {Mobile} verified. Routing to Full Authentication Path.", request.Mobile);
                
                var activeMapping = user.HospitalMappings.FirstOrDefault(m => m.IsDefault) 
                                   ?? user.HospitalMappings.FirstOrDefault();

                if (activeMapping != null)
                {
                    var authorizedHospitalIds = user.HospitalMappings.Select(m => m.HospitalId).ToList();
                    var accessToken = _jwtProvider.GenerateContextualToken(user, activeMapping, authorizedHospitalIds);
                    var refreshToken = _jwtProvider.GenerateRefreshToken();

                    // Persist Refresh Token
                    var refreshTokenEntity = new _1Rad.Domain.Entities.RefreshToken
                    {
                        UserId = user.UserId,
                        Token = refreshToken,
                        ExpiresAt = DateTime.UtcNow.AddDays(7)
                    };
                    _context.RefreshTokens.Add(refreshTokenEntity);
                    await _context.SaveChangesAsync(cancellationToken);

                    return new VerifyOTPResponse(
                        Success: true,
                        Token: accessToken,
                        RefreshToken: refreshToken,
                        IsRegistered: true,
                        User: new UserDetailsDto(
                        user.UserId,
                        user.FullName, 
                        user.Email, 
                        user.Mobile, 
                        string.Join(", ", activeMapping.Roles.Select(r => r.RoleName).DefaultIfEmpty("User")),
                        user.HospitalMappings.Select(m => new AuthorizedHospitalDto(
                            m.HospitalId,
                            m.Hospital?.HospitalName ?? "Unknown Hub",
                            string.Join(", ", m.Roles.Select(r => r.RoleName).DefaultIfEmpty("User")),
                            m.IsDefault
                        )).ToList())
                    );
                }
            }

            _logger.LogInformation("New or Pending user {Mobile} verified. Routing to Identity Initiation Path.", request.Mobile);
            var initiationToken = _jwtProvider.GenerateInitiationToken(request.Mobile, user?.UserId);
            
            return new VerifyOTPResponse(
                Success: true, 
                Token: initiationToken, 
                IsRegistered: false,
                Message: "OTP verified. Please complete your registration."
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during OTP verification for {Mobile}", request.Mobile);
            return new VerifyOTPResponse(false, Message: "A system error occurred during verification.");
        }
    }
}
