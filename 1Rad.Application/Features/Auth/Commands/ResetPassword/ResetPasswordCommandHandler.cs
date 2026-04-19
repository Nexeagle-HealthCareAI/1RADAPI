using _1Rad.Application.Interfaces;
using _1Rad.Domain.Events;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace _1Rad.Application.Features.Auth.Commands.ResetPassword;

public class ResetPasswordCommandHandler : IRequestHandler<ResetPasswordCommand, (bool Success, string? Error)>
{
    private readonly IApplicationDbContext _context;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtProvider _jwtProvider;

    public ResetPasswordCommandHandler(IApplicationDbContext context, IPasswordHasher hasher, IJwtProvider jwtProvider)
    {
        _context = context;
        _hasher = hasher;
        _jwtProvider = jwtProvider;
    }

    public async Task<(bool Success, string? Error)> Handle(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        try
        {
            // 1. Verify Reset JWT using IJwtProvider
            string token = request.ResetToken;
            var principal = _jwtProvider.ValidateToken(token, "password-reset");
            if (principal == null)
            {
                return (false, "Invalid or expired reset token.");
            }

            // Try both 'sub' and NameIdentifier due to potential claim mapping by JwtSecurityTokenHandler
            var claim = principal.FindFirst("sub") ?? principal.FindFirst(ClaimTypes.NameIdentifier);
            string? userIdClaim = claim?.Value;

            if (string.IsNullOrEmpty(userIdClaim))
            {
                return (false, "Invalid token claims.");
            }

            if (!Guid.TryParse(userIdClaim, out Guid userId))
            {
                return (false, "Invalid user identity in token.");
            }

            // 2. Resolve User
            var user = await _context.Users.FirstOrDefaultAsync(u => u.UserId == userId, cancellationToken);
            if (user == null)
            {
                return (false, "Account not found.");
            }

            // 3. Update Password
            user.PasswordHash = _hasher.Hash(request.NewPassword);

            // 4. Invalidate Sessions
            var tokensToRemove = await _context.RefreshTokens
                .Where(t => t.UserId == userId)
                .ToListAsync(cancellationToken);
            
            if (tokensToRemove.Any())
            {
                _context.RefreshTokens.RemoveRange(tokensToRemove);
            }

            // 5. Audit Event
            user.AddDomainEvent(new UserPasswordChangedEvent(user));

            await _context.SaveChangesAsync(cancellationToken);

            return (true, null);
        }
        catch (Exception)
        {
            return (false, "An error occurred while resetting the password.");
        }
    }
}
