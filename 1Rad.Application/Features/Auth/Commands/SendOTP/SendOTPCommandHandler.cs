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

    public async Task<SendOTPResponse> Handle(SendOTPCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initiating OTP send request for mobile: {Mobile}", request.Mobile);

        try
        {
            // 1. Check for existing active user
            var existingUser = await _context.Users
                .FirstOrDefaultAsync(u => u.Mobile == request.Mobile, cancellationToken);
            
            if (existingUser != null && existingUser.Status == _1Rad.Domain.Enums.UserStatus.Active)
            {
                _logger.LogInformation("User {Mobile} is already registered and active. Redirecting to login path.", request.Mobile);
                return new SendOTPResponse(Success: true, IsAlreadyRegistered: true);
            }

            // 2. Generate a secure 6-digit passcode
            var otp = new Random().Next(100000, 999999).ToString();
            
            // 3. Hash the passcode
            var hash = _hasher.Hash(otp);
            
            // 4. Store in OTPVerifications
            var verification = new OTPVerification
            {
                Identifier = request.Mobile,
                CodeHash = hash,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5),
                IsUsed = false,
                Purpose = "Registration"
            };

            _context.OTPVerifications.Add(verification);
            await _context.SaveChangesAsync(cancellationToken);
            
            _logger.LogDebug("OTP verification record saved for {Mobile}", request.Mobile);

            // 5. Dispatch SMS
            await _sms.SendOtpAsync(request.Mobile, otp);
            
            _logger.LogInformation("OTP successfully dispatched to {Mobile}", request.Mobile);
            return new SendOTPResponse(Success: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while sending OTP to {Mobile}", request.Mobile);
            return new SendOTPResponse(Success: false, Message: ex.Message);
        }
    }
}
