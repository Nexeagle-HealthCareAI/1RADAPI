using MediatR;

namespace _1Rad.Application.Features.Auth.Commands.VerifyResetCode;

public record VerifyResetCodeCommand(string Identifier, string Code) : IRequest<(bool Success, string? ResetToken, string? Error)>;
