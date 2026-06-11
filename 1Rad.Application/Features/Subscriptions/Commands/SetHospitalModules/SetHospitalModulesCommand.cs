using MediatR;

namespace _1Rad.Application.Features.Subscriptions.Commands.SetHospitalModules;

/// <summary>
/// Sets the active center's product modules (RIS / PACS). Removing PACS starts
/// the downgrade grace clock (PacsRemovedAt); re-adding it clears the clock.
/// </summary>
public record SetHospitalModulesCommand(string Modules) : IRequest<SetHospitalModulesResponse>;

public class SetHospitalModulesResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Modules { get; set; }
    public DateTime? PacsRemovedAt { get; set; }
}
