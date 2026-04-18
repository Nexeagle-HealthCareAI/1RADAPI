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

    public async Task<(Guid? UserId, string? Token, string? Error)> Handle(IdentitySetupCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing Identity Setup for mobile: {Mobile}", request.Mobile);

        try
        {
            // 1. Uniqueness check
            if (await _context.Users.AnyAsync(u => u.Email == request.Email, cancellationToken))
            {
                _logger.LogWarning("Email already in use: {Email}", request.Email);
                return (null, null, "Email already in use.");
            }

            if (await _context.Users.AnyAsync(u => u.Mobile == request.Mobile, cancellationToken))
            {
                _logger.LogWarning("Mobile already in use: {Mobile}", request.Mobile);
                return (null, null, "Mobile already in use.");
            }

            // 2. Hash Password
            var passwordHash = _hasher.Hash(request.Password);

            // 3. Create User
            var user = new User
            {
                FullName = request.FullName,
                Email = request.Email,
                Mobile = request.Mobile,
                PasswordHash = passwordHash,
                Status = UserStatus.Pending,
                IsVerified = false // verified after infra deployment in Stage 3
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("New user created successfully. ID: {UserId}", user.UserId);

            // 4. Return new JWT with UserId
            var token = _jwtProvider.GenerateInitiationToken(user.Mobile, user.UserId);

            return (user.UserId, token, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during identity setup for {Mobile}", request.Mobile);
            return (null, null, $"Internal error: {ex.Message}");
        }
    }
}
