namespace _1Rad.Application.Interfaces;

public interface ISmsService
{
    Task SendOtpAsync(string mobile, string otp);
}
