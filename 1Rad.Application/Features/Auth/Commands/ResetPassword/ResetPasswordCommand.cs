using MediatR;

namespace _1Rad.Application.Features.Auth.Commands.ResetPassword;

public record ResetPasswordCommand(string ResetToken, string NewPassword) : IRequest<(bool Success, string? Error)>;
