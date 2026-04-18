using MediatR;

namespace _1Rad.Application.Features.Auth.Commands.SendOTP;

public record SendOTPCommand(string Mobile) : IRequest<bool>;
