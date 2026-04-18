using MediatR;

namespace _1Rad.Application.Features.Auth.Commands.IdentitySetup;

public record IdentitySetupCommand(
    string FullName, 
    string Email, 
    string Mobile, 
    string Password) : IRequest<(Guid? UserId, string? Token, string? Error)>;
