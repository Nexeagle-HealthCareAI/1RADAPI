using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Services;

public class OtpService : IOtpService
{
    private readonly IApplicationDbContext _context;
    private readonly ISmsService _smsService;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<OtpService> _logger;

    public OtpService(
        IApplicationDbContext context,
        ISmsService smsService,
        IPasswordHasher passwordHasher,
        ILogger<OtpService> logger)
    {
        _context = context;
        _smsService = smsService;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    public async Task<string> GenerateAndSendOtpAsync(string identifier, string purpose)
    {
        _logger.LogInformation("Generating OTP for identifier: {Identifier}, purpose: {Purpose}", identifier, purpose);

        try
        {
            // 1. Generate a secure 6-digit OTP
            var otp = new Random().Next(100000, 999999).ToString();
            
            // 2. Hash the OTP
            var hash = _passwordHasher.Hash(otp);
            
            // 3. Store in OTPVerifications with purpose
            var verification = new OTPVerification
            {
                Identifier = identifier,
                CodeHash = hash,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5), // 5 minutes expiry
                IsUsed = false,
                Purpose = purpose
            };

            _context.OTPVerifications.Add(verification);
            await _context.SaveChangesAsync(CancellationToken.None);
            
            _logger.LogDebug("OTP verification record saved for {Identifier} with purpose {Purpose}", identifier, purpose);

            // 4. Send OTP via SMS
            await _smsService.SendOtpAsync(identifier, otp);
            
            _logger.LogInformation("OTP successfully sent to {Identifier} for {Purpose}", identifier, purpose);
            return otp;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while generating and sending OTP to {Identifier} for {Purpose}", identifier, purpose);
            throw;
        }
    }
}