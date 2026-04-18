using MediatR;

namespace _1Rad.Application.Features.Auth.Commands.SwitchContext;

public record SwitchContextCommand(Guid TargetHospitalId) : IRequest<SwitchContextResponse>;

public class SwitchContextResponse
{
    public bool Success { get; set; }
    public string? AccessToken { get; set; }
    public string? Error { get; set; }
}
