using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Auth.Commands.IdentitySetup;

public class IdentitySetupCommandHandler : IRequestHandler<IdentitySetupCommand, (Guid? UserId, string? Token, string? Error)>
{
    private readonly IApplicationDbContext _context;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtProvider _jwtProvider;
    private readonly ILogger<IdentitySetupCommandHandler> _logger;

    public IdentitySetupCommandHandler(IApplicationDbContext context, IPasswordHasher hasher, IJwtProvider jwtProvider, ILogger<IdentitySetupCommandHandler> logger)
    {
        _context = context;
        _hasher = hasher;
        _jwtProvider = jwtProvider;
        _logger = logger;
    }

    public async Task<IdentitySetupResponse> Handle(IdentitySetupCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing Identity Setup for mobile: {Mobile}", request.Mobile);

        try
        {
            // 1. Check for existing user
            var existingUser = await _context.Users
                .FirstOrDefaultAsync(u => u.Email == request.Email || u.Mobile == request.Mobile, cancellationToken);

            if (existingUser != null)
            {
                if (existingUser.Status == UserStatus.Active)
                {
                    _logger.LogWarning("Identity attempt for already active user: {Email}/{Mobile}", request.Email, request.Mobile);
                    return new IdentitySetupResponse { 
                        Error = "This identity is already fully registered and active. Please proceed to login.",
                        ErrorCode = "IDENTITY_ALREADY_ACTIVE"
                    };
                }

                _logger.LogInformation("Updating existing Pending user: {UserId}", existingUser.UserId);
                
                // Update credentials for fresh start
                existingUser.FullName = request.FullName;
                existingUser.Email = request.Email;
                existingUser.Mobile = request.Mobile;
                existingUser.PasswordHash = _hasher.Hash(request.Password);
                
                await _context.SaveChangesAsync(cancellationToken);

                var updateToken = _jwtProvider.GenerateInitiationToken(existingUser.Mobile, existingUser.UserId);
                return new IdentitySetupResponse { UserId = existingUser.UserId, Token = updateToken };
            }

            // 2. Create new User if none exists
            var passwordHash = _hasher.Hash(request.Password);
            var user = new User
            {
                FullName = request.FullName,
                Email = request.Email,
                Mobile = request.Mobile,
                PasswordHash = passwordHash,
                Status = UserStatus.Pending,
                IsVerified = false
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("New user created successfully. ID: {UserId}", user.UserId);

            // 4. Return new JWT with UserId
            var token = _jwtProvider.GenerateInitiationToken(user.Mobile, user.UserId);

            return new IdentitySetupResponse { UserId = user.UserId, Token = token };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during identity setup for {Mobile}", request.Mobile);
            return new IdentitySetupResponse { Error = $"Critical registration error: {ex.Message}", ErrorCode = "INTERNAL_ERROR" };
        }
    }
}
