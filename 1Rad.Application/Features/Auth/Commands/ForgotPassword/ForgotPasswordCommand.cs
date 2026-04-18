using MediatR;

namespace _1Rad.Application.Features.Auth.Commands.ForgotPassword;

public record ForgotPasswordCommand(string Identifier) : IRequest<(bool Success, string? Message)>;
