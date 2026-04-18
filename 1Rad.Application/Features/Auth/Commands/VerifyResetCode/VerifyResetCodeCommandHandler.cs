using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Auth.Commands.VerifyResetCode;

public class VerifyResetCodeCommandHandler : IRequestHandler<VerifyResetCodeCommand, (bool Success, string? ResetToken, string? Error)>
{
    private readonly IApplicationDbContext _context;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtProvider _jwtProvider;

    public VerifyResetCodeCommandHandler(IApplicationDbContext _context, IPasswordHasher _hasher, IJwtProvider _jwtProvider)
    {
        this._context = _context;
        this._hasher = _hasher;
        this._jwtProvider = _jwtProvider;
    }

    public async Task<(bool Success, string? ResetToken, string? Error)> Handle(VerifyResetCodeCommand request, CancellationToken cancellationToken)
    {
        var verification = await _context.OTPVerifications
            .Where(x => x.Identifier == request.Identifier && x.Purpose == "PasswordReset" && !x.IsUsed && x.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(x => x.ExpiresAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (verification == null)
            return (false, null, "The verification code is invalid or has expired.");

        if (!_hasher.Verify(request.Code, verification.CodeHash))
            return (false, null, "Invalid verification code.");

        // Consume OTP
        verification.IsUsed = true;
        await _context.SaveChangesAsync(cancellationToken);

        // Resolve User
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Email == request.Identifier || u.Mobile == request.Identifier, cancellationToken);
        
        if (user == null) return (false, null, "User synchronization error.");

        // Generate Short-lived Reset Token
        var resetToken = _jwtProvider.GenerateResetToken(user.UserId);

        return (true, resetToken, null);
    }
}
