using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Auth.Commands.ForgotPassword;

public class ForgotPasswordCommandHandler : IRequestHandler<ForgotPasswordCommand, (bool Success, string? Message)>
{
    private readonly IApplicationDbContext _context;
    private readonly IOtpService _otpService;
    private readonly ILogger<ForgotPasswordCommandHandler> _logger;

    public ForgotPasswordCommandHandler(IApplicationDbContext _context, IOtpService _otpService, ILogger<ForgotPasswordCommandHandler> _logger)
    {
        this._context = _context;
        this._otpService = _otpService;
        this._logger = _logger;
    }

    public async Task<(bool Success, string? Message)> Handle(ForgotPasswordCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing forgot password request for {Identifier}", request.Identifier);

        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Email == request.Identifier || u.Mobile == request.Identifier, cancellationToken);

        // Security Architecture: Always return success even if user not found to prevent enumeration
        if (user == null)
        {
            _logger.LogWarning("Forgot password attempted for non-existent user: {Identifier}", request.Identifier);
            return (true, "If your account exists, a verification code has been sent.");
        }

        // Generate OTP with Recovery Purpose
        var otp = await _otpService.GenerateAndSendOtpAsync(request.Identifier, "PasswordReset");

        return (true, "Verification code sent to your registered contact node.");
    }
}
