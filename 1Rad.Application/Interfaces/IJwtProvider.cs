namespace _1Rad.Application.Interfaces;

public interface IJwtProvider
{
    string GenerateInitiationToken(string mobile, Guid? userId = null);
    string GenerateAccessToken(Guid userId, string mobile, string email, string role);
}
