using MediatR;

namespace _1Rad.Application.Features.Auth.Commands.VerifyOTP;

public record VerifyOTPCommand(string Mobile, string Code) : IRequest<VerifyOTPResponse>;
// Returns the Initiation JWT if successful, otherwise null
