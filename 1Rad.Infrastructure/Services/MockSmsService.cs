using _1Rad.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Services;

public class MockSmsService : ISmsService
{
    private readonly ILogger<MockSmsService> _logger;

    public MockSmsService(ILogger<MockSmsService> logger)
    {
        _logger = logger;
    }

    public Task SendOtpAsync(string mobile, string otp)
    {
        _logger.LogInformation("Sending OTP {Otp} to mobile number {Mobile} via MockSmsService (WhatsAPI integration pending).", otp, mobile);
        return Task.CompletedTask;
    }
}
