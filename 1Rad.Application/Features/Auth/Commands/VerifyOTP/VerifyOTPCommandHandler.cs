using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Auth.Commands.VerifyOTP;

public class VerifyOTPCommandHandler : IRequestHandler<VerifyOTPCommand, string?>
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

    public async Task<string?> Handle(VerifyOTPCommand request, CancellationToken cancellationToken)
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
                return null;
            }

            if (!_hasher.Verify(request.Code, verification.CodeHash))
            {
                _logger.LogWarning("Invalid OTP code provided for mobile: {Mobile}", request.Mobile);
                return null;
            }

            verification.IsUsed = true;
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("OTP verified and marked as used for {Mobile}", request.Mobile);

            var token = _jwtProvider.GenerateInitiationToken(request.Mobile);
            return token;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during OTP verification for {Mobile}", request.Mobile);
            return null;
        }
    }
}
