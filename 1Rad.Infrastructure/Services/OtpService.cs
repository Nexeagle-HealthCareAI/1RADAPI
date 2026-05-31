using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Services;

public class OtpService : IOtpService
{
    private readonly IApplicationDbContext _context;
    private readonly ISmsService _smsService;
    private readonly IEmailService _emailService;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<OtpService> _logger;

    public OtpService(
        IApplicationDbContext context,
        ISmsService smsService,
        IEmailService emailService,
        IPasswordHasher passwordHasher,
        ILogger<OtpService> logger)
    {
        _context = context;
        _smsService = smsService;
        _emailService = emailService;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    private static bool IsEmail(string identifier) =>
        !string.IsNullOrWhiteSpace(identifier) && identifier.Contains('@');

    public async Task<string> GenerateAndSendOtpAsync(string identifier, string purpose)
    {
        _logger.LogInformation("Generating OTP for identifier: {Identifier}, purpose: {Purpose}", identifier, purpose);

        try
        {
            var otp = new Random().Next(100000, 999999).ToString();
            var hash = _passwordHasher.Hash(otp);

            var verification = new OTPVerification
            {
                Identifier = identifier,
                CodeHash = hash,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5),
                IsUsed = false,
                Purpose = purpose
            };

            _context.OTPVerifications.Add(verification);
            await _context.SaveChangesAsync(CancellationToken.None);

            _logger.LogDebug("OTP verification record saved for {Identifier} with purpose {Purpose}", identifier, purpose);

            // Route by identifier shape: email addresses get an email, everything
            // else goes through the SMS/WhatsApp gateway. The earlier
            // implementation always used SMS, which silently dropped OTPs for
            // any email-based identifier (e.g. password recovery by email).
            if (IsEmail(identifier))
            {
                var subject = purpose switch
                {
                    "PasswordReset" => "1Rad password recovery code",
                    "Registration"  => "1Rad verification code",
                    _               => "1Rad secure access code",
                };

                var body = BuildOtpEmailBody(otp, purpose);
                await _emailService.SendEmailAsync(identifier, subject, body);
                _logger.LogInformation("OTP successfully emailed to {Identifier} for {Purpose}", identifier, purpose);
            }
            else
            {
                await _smsService.SendOtpAsync(identifier, otp);
                _logger.LogInformation("OTP successfully SMS-dispatched to {Identifier} for {Purpose}", identifier, purpose);
            }

            return otp;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while generating and sending OTP to {Identifier} for {Purpose}", identifier, purpose);
            throw;
        }
    }

    private static string BuildOtpEmailBody(string otp, string purpose)
    {
        var headline = purpose == "PasswordReset"
            ? "Password recovery code"
            : "Verification code";
        return $@"<!doctype html>
<html><body style='margin:0;padding:24px;background:#0f172a;font-family:Segoe UI,Helvetica,Arial,sans-serif;color:#e2e8f0;'>
  <div style='max-width:480px;margin:0 auto;background:linear-gradient(160deg,rgba(255,255,255,0.04),rgba(255,255,255,0.01));border:1px solid rgba(255,255,255,0.10);border-radius:18px;padding:32px;'>
    <div style='font-size:11px;font-weight:800;letter-spacing:3px;color:#00f2fe;text-transform:uppercase;margin-bottom:12px;'>1RAD &middot; NexEagle</div>
    <h2 style='color:#fff;margin:0 0 6px;font-size:20px;font-weight:800;letter-spacing:-0.3px;'>{headline}</h2>
    <p style='color:rgba(255,255,255,0.62);font-size:13px;margin:0 0 24px;line-height:1.5;'>Use the code below to continue. It expires in 5 minutes.</p>
    <div style='background:rgba(0,242,254,0.08);border:1px solid rgba(0,242,254,0.25);border-radius:12px;padding:18px;text-align:center;'>
      <div style='font-size:32px;font-weight:900;letter-spacing:10px;color:#00f2fe;'>{otp}</div>
    </div>
    <p style='color:rgba(255,255,255,0.40);font-size:11px;margin-top:24px;line-height:1.5;'>If you did not request this, you can safely ignore this email. Someone may have typed your address by mistake.</p>
  </div>
</body></html>";
    }
}