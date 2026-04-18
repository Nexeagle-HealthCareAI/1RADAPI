using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Auth.Commands.SendOTP;

public class SendOTPCommandHandler : IRequestHandler<SendOTPCommand, bool>
{
    private readonly IApplicationDbContext _context;
    private readonly ISmsService _sms;
    private readonly IPasswordHasher _hasher;
    private readonly ILogger<SendOTPCommandHandler> _logger;

    public SendOTPCommandHandler(IApplicationDbContext context, ISmsService sms, IPasswordHasher hasher, ILogger<SendOTPCommandHandler> logger)
    {
        _context = context;
        _sms = sms;
        _hasher = hasher;
        _logger = logger;
    }

    public async Task<bool> Handle(SendOTPCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initiating OTP send request for mobile: {Mobile}", request.Mobile);

        try
        {
            // 1. Generate a secure 6-digit passcode
            var otp = new Random().Next(100000, 999999).ToString();
            
            // 2. Hash the passcode
            var hash = _hasher.Hash(otp);
            
            // 3. Store in OTPVerifications
            var verification = new OTPVerification
            {
                Identifier = request.Mobile,
                CodeHash = hash,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5),
                IsUsed = false
            };

            _context.OTPVerifications.Add(verification);
            await _context.SaveChangesAsync(cancellationToken);
            
            _logger.LogDebug("OTP verification record saved for {Mobile}", request.Mobile);

            // 4. Dispatch SMS
            await _sms.SendOtpAsync(request.Mobile, otp);
            
            _logger.LogInformation("OTP successfully dispatched to {Mobile}", request.Mobile);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while sending OTP to {Mobile}", request.Mobile);
            return false;
        }
    }
}
