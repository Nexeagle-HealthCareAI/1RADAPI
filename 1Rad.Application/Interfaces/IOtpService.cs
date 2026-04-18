namespace _1Rad.Application.Interfaces;

public interface IOtpService
{
    Task<string> GenerateAndSendOtpAsync(string identifier, string purpose);
}