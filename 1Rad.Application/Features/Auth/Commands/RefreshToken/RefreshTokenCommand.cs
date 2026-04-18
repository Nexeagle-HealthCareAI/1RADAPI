using MediatR;

namespace _1Rad.Application.Features.Auth.Commands.TokenRefresh;

public record RefreshTokenCommand(string RefreshToken) : IRequest<RefreshTokenResponse>;

public class RefreshTokenResponse
{
    public bool Success { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? Error { get; set; }
}
