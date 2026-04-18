using MediatR;

namespace _1Rad.Application.Features.Auth.Commands.SendOTP;

public record SendOTPResponse(bool Success, bool IsAlreadyRegistered = false, string? Message = null);

public record SendOTPCommand(string Mobile) : IRequest<SendOTPResponse>;
