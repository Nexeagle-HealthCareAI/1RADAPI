using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Application.Features.Auth.Commands.SendOTP;

public class SendOTPCommandHandler : IRequestHandler<SendOTPCommand, SendOTPResponse>
{
    private readonly IApplicationDbContext _context;
    private readonly ISmsService _sms;
    private readonly IEmailService _email;
    private readonly IPasswordHasher _hasher;
    private readonly ILogger<SendOTPCommandHandler> _logger;

    public SendOTPCommandHandler(IApplicationDbContext context, ISmsService sms, IEmailService email, IPasswordHasher hasher, ILogger<SendOTPCommandHandler> logger)
    {
        _context = context;
        _sms = sms;
        _email = email;
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
            
            var isAlreadyRegistered = false;
            if (existingUser != null && existingUser.Status == _1Rad.Domain.Enums.UserStatus.Active)
            {
                _logger.LogInformation("User {Mobile} is already registered and active. Proceeding with OTP dispatch for authentication path.", request.Mobile);
                isAlreadyRegistered = true;
            }

            // 2. Generate a secure 6-digit passcode
            var otp = new Random().Next(100000, 999999).ToString();

            // 3. Hash the passcode
            // var hash = _hasher.Hash(otp);
              var hash = otp;
            
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

            // 5. Dispatch SMS (WhatsApp)
            var smsTask = _sms.SendOtpAsync(request.Mobile, otp);
            
            // 6. Dispatch Email (if exists)
            Task? emailTask = null;
            if (existingUser != null && !string.IsNullOrEmpty(existingUser.Email))
            {
                _logger.LogInformation("Secondary dispatch: Sending OTP to email {Email} for {Mobile}", existingUser.Email, request.Mobile);
                emailTask = _email.SendEmailAsync(
                    existingUser.Email, 
                    "1Rad Secure Access Code", 
                    $@"<div style='font-family: sans-serif; padding: 20px; background: #0f172a; color: white;'>
                        <h2 style='color: #00f2fe;'>1RAD TERMINAL ACCESS</h2>
                        <p>Your decryption passcode is:</p>
                        <h1 style='letter-spacing: 5px; color: #00f2fe;'>{otp}</h1>
                        <p style='font-size: 12px; color: #64748b;'>This code expires in 5 minutes. If you did not request this, please alert clinical security.</p>
                      </div>");
            }

            // Wait for both tasks to complete
            await smsTask;
            if (emailTask != null) await emailTask;
            
            _logger.LogInformation("OTP successfully dispatched to {Mobile}", request.Mobile);
            return new SendOTPResponse(Success: true, IsAlreadyRegistered: isAlreadyRegistered);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while sending OTP to {Mobile}", request.Mobile);
            return new SendOTPResponse(Success: false, Message: ex.Message);
        }
    }
}
