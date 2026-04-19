using MediatR;

namespace _1Rad.Application.Features.Auth.Commands.IdentitySetup;

public record IdentitySetupCommand(
    string FullName, 
    string Email, 
    string Mobile, 
    string Password) : IRequest<IdentitySetupResponse>;

public class IdentitySetupResponse
{
    public Guid? UserId { get; set; }
    public string? Token { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}
